using Magent.Cli;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magent.Core;
using Magent.Data;
using Magent.Esi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
{
    options.TimestampFormat = command == "intel" ? string.Empty : "yyyy-MM-dd HH:mm:ss ";
}));
var logger = loggerFactory.CreateLogger("magent");

var app = new MagentApp(loggerFactory);

return command switch
{
    "init" => await app.InitAsync(),
    "auth" => await app.AuthAsync(),
    "sync" => await app.SyncAsync(),
    "run" => await app.RunAsync(),
    "report" => await app.ReportAsync(),
    "serve" => await app.ServeAsync(),
    "intel" => await app.IntelAsync(args.Skip(1).ToArray()),
    _ => PrintHelp(logger)
};

static int PrintHelp(ILogger logger)
{
    logger.LogInformation("Commands: magent init | auth | sync | run | report | serve | intel watch | intel paste");
    return 0;
}

internal sealed class MagentApp(ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MagentApp>();
    private readonly string _root = Directory.GetCurrentDirectory();
    private readonly TokenStore _tokenStore = new(loggerFactory.CreateLogger<TokenStore>());

    public Task<int> InitAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".magent"));

        var configPath = Path.Combine(_root, "config", "config.json");
        if (!File.Exists(configPath))
        {
            var config = new AppConfig(10000043, 60008494, 15, 3m, 4.5m, 2m, 50, 250, 500_000_000m, 25m, 20_000_000m, 10, null, new IntelConfig());
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }

        var db = new SqliteStore(Path.Combine(_root, "data", "magent.db"));
        db.Initialize();
        _logger.LogInformation("Initialized folders, config, and SQLite schema.");
        return Task.FromResult(0);
    }

    public async Task<int> AuthAsync()
    {
        var esi = CreateEsiClient();
        try
        {
            var result = await esi.AuthorizeAsync(new Uri("http://localhost"), CancellationToken.None);
            await _tokenStore.SaveRefreshTokenAsync(result.RefreshToken, CancellationToken.None);
            _logger.LogInformation("Auth complete for character {CharacterName} ({CharacterId}).", result.CharacterName, result.CharacterId);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auth flow failed. Ensure refresh token is available in local secure storage.");
            return 1;
        }
    }

    public async Task<int> SyncAsync()
    {
        var config = LoadConfig();
        var db = CreateStore();
        db.Initialize();
        var esi = CreateEsiClient();

        var token = await _tokenStore.LoadRefreshTokenAsync(CancellationToken.None);
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("No refresh token found. Run 'magent auth' first.");
            return 1;
        }

        var orders = await SafeCall(() => esi.GetCharacterOrdersAsync(token, CancellationToken.None), [], "character orders");
        db.ReplaceCharacterOrders(orders);

        var watchlist = orders.Select(x => x.TypeId).Distinct().Take(config.MaxWatchlistSize).ToList();
        db.SaveWatchlist(watchlist);

        foreach (var typeId in watchlist)
        {
            var marketOrders = await SafeCall(() => esi.GetMarketOrdersAsync(config.RegionId, typeId, null, CancellationToken.None), [], $"market orders for {typeId}");
            db.SaveOrderbookSnapshot(typeId, marketOrders.Where(x => x.LocationId == config.HubLocationId).ToList());
        }

        _logger.LogInformation("Sync complete. Character orders: {Count}, watchlist: {WatchlistCount}", orders.Count, watchlist.Count);
        return 0;
    }

    public async Task<int> RunAsync()
    {
        var config = LoadConfig();
        while (true)
        {
            var result = await SyncAsync();
            if (result == 0)
            {
                await GenerateReportAndAlertsAsync(config);
            }

            _logger.LogInformation("Sleeping for {Minutes} minutes", config.PollIntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(config.PollIntervalMinutes));
        }
    }

    public async Task<int> ReportAsync()
    {
        var config = LoadConfig();
        await GenerateReportAndAlertsAsync(config, dedupeAlerts: false);
        return 0;
    }

    public async Task<int> IntelAsync(string[] intelArgs)
    {
        var mode = intelArgs.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        var config = LoadConfig();
        var db = CreateStore();
        db.Initialize();
        var runner = new IntelRunner(_logger, db, CreateEsiClient(), _root);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        return mode switch
        {
            "watch" => await runner.WatchAsync(config, GetOption(intelArgs, "--chatlog-path"), cts.Token),
            "paste" => await runner.PasteAsync(config, await ReadNamesAsync(intelArgs.Skip(1).ToArray()), null, cts.Token),
            _ => 0
        };
    }

    private static string? GetOption(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(string[] args)
    {
        var cleaned = args.Where(x => !x.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (cleaned.Count == 1 && cleaned[0].Contains(','))
        {
            return cleaned[0].Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        if (cleaned.Count > 0)
        {
            return cleaned.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }

        if (!Console.IsInputRedirected)
        {
            return [];
        }

        var input = await Console.In.ReadToEndAsync();
        return input.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<int> ServeAsync()
    {
        var db = CreateStore();
        db.Initialize();
        var esi = CreateEsiClient();
        var typeNameCache = new Dictionary<int, string>();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");

        var web = builder.Build();
        web.MapGet("/", () => Results.Content(WebDashboardHtml, "text/html"));

        web.MapGet("/api/dashboard", async () =>
        {
            var watchlist = db.GetWatchlist();
            var latestOrders = db.GetLatestMarketOrders();
            var opportunities = db.GetLatestOpportunities(100)
                .GroupBy(x => x.Fingerprint)
                .Select(x => x.First())
                .OrderByDescending(x => x.DetectedAt)
                .ToList();

            var typeIds = watchlist.Concat(opportunities.Select(x => x.TypeId)).Distinct().ToList();
            var unknownIds = typeIds.Where(id => !typeNameCache.ContainsKey(id)).ToList();
            if (unknownIds.Count > 0)
            {
                var resolved = await ResolveTypeNamesAsync(esi, unknownIds);
                foreach (var pair in resolved)
                {
                    typeNameCache[pair.Key] = pair.Value;
                }
            }

            var bestPrices = latestOrders
                .GroupBy(x => x.TypeId)
                .ToDictionary(
                    x => x.Key,
                    x => new
                    {
                        BestBuy = x.Where(o => o.IsBuyOrder).Select(o => o.Price).DefaultIfEmpty(0m).Max(),
                        BestSell = x.Where(o => !o.IsBuyOrder).Select(o => o.Price).DefaultIfEmpty(0m).Min()
                    });

            var payload = opportunities.Select(item => new
            {
                item.Fingerprint,
                item.TypeId,
                TypeName = typeNameCache.GetValueOrDefault(item.TypeId) ?? $"Type {item.TypeId}",
                Kind = item.Kind.ToString(),
                item.NetMarginPct,
                item.EstimatedProfitIsk,
                Confidence = item.Confidence.ToString(),
                item.DetectedAt,
                BestBuy = bestPrices.GetValueOrDefault(item.TypeId)?.BestBuy ?? 0m,
                BestSell = bestPrices.GetValueOrDefault(item.TypeId)?.BestSell ?? 0m,
                Spread = (bestPrices.GetValueOrDefault(item.TypeId)?.BestSell ?? 0m) - (bestPrices.GetValueOrDefault(item.TypeId)?.BestBuy ?? 0m)
            });

            var watchlistPayload = watchlist.Select(typeId => new
            {
                TypeId = typeId,
                TypeName = typeNameCache.GetValueOrDefault(typeId) ?? $"Type {typeId}"
            });

            return Results.Json(new { Watchlist = watchlistPayload, Opportunities = payload });
        });

        web.MapPost("/api/watchlist", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<WatchlistUpdateRequest>();
            if (body is null || body.TypeId <= 0)
            {
                return Results.BadRequest(new { error = "typeId must be a positive integer." });
            }

            db.AddWatchItem(body.TypeId);
            return Results.Ok(new { body.TypeId });
        });

        web.MapDelete("/api/watchlist/{typeId:int}", (int typeId) =>
        {
            if (typeId <= 0)
            {
                return Results.BadRequest(new { error = "typeId must be a positive integer." });
            }

            return db.RemoveWatchItem(typeId) ? Results.Ok() : Results.NotFound();
        });

        web.MapPost("/api/export", async (HttpContext context) =>
        {
            var body = await context.Request.ReadFromJsonAsync<ExportRequest>();
            if (body is null || body.TypeIds.Count == 0)
            {
                return Results.BadRequest(new { error = "Provide at least one typeId." });
            }

            var latestOrders = db.GetLatestMarketOrders();
            var lines = new List<string>();

            var selected = body.TypeIds.Distinct().ToList();
            var typeNames = await ResolveTypeNamesAsync(esi, selected);

            foreach (var typeId in selected)
            {
                var orders = latestOrders.Where(x => x.TypeId == typeId).ToList();
                if (orders.Count == 0)
                {
                    continue;
                }

                var name = typeNames.GetValueOrDefault(typeId) ?? $"Type {typeId}";
                var quantity = Math.Max(body.QuantityPerItem ?? 1, 1);
                if (string.Equals(body.Format, "eve-multibuy", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"{name}\t{quantity}");
                    continue;
                }

                if (string.Equals(body.Side, "buy", StringComparison.OrdinalIgnoreCase))
                {
                    var bestPrice = orders.Where(x => !x.IsBuyOrder).Select(x => x.Price).DefaultIfEmpty(0m).Min();
                    lines.Add($"BUY\t{name}\t{typeId}\t{bestPrice:0.00}\t{quantity}");
                }
                else
                {
                    var bestPrice = orders.Where(x => x.IsBuyOrder).Select(x => x.Price).DefaultIfEmpty(0m).Max();
                    lines.Add($"SELL\t{name}\t{typeId}\t{bestPrice:0.00}\t{quantity}");
                }
            }

            return Results.Json(new { Clipboard = string.Join(Environment.NewLine, lines) });
        });

        _logger.LogInformation("Dashboard available at http://localhost:5000");
        await web.RunAsync("http://0.0.0.0:5000");
        return 0;
    }

    private async Task GenerateReportAndAlertsAsync(AppConfig config, bool dedupeAlerts = true)
    {
        var db = CreateStore();
        var esi = CreateEsiClient();

        var characterOrders = db.GetCharacterOrders();
        var marketOrders = db.GetLatestMarketOrders().Where(x => x.LocationId == config.HubLocationId).ToList();
        var watchlist = db.GetWatchlist(config.MaxWatchlistSize);

        var historyTasks = watchlist.ToDictionary(
            typeId => typeId,
            typeId => SafeCall(
                () => esi.GetMarketHistoryAsync(config.RegionId, typeId, null, CancellationToken.None),
                [],
                $"market history for {typeId}"));

        await Task.WhenAll(historyTasks.Values);
        var dailyVolumes = historyTasks.ToDictionary(
            x => x.Key,
            x => x.Value.Result.Count == 0 ? 0L : (long)x.Value.Result.TakeLast(7).Average(p => p.Volume));

        var token = await _tokenStore.LoadRefreshTokenAsync(CancellationToken.None);
        var walletBalance = string.IsNullOrWhiteSpace(token)
            ? 0
            : await SafeCall(() => esi.GetWalletBalanceAsync(token, CancellationToken.None), 0m, "wallet balance");

        var calculator = new OpportunityCalculator();
        var now = DateTimeOffset.UtcNow;
        var opportunities = calculator.Calculate(config, characterOrders, marketOrders, dailyVolumes, walletBalance, now)
            .OrderByDescending(x => x.SuggestedInvestmentIsk)
            .ThenByDescending(x => x.NetMarginPct)
            .Take(config.MaxOrdersPerCycle)
            .ToList();
        db.SaveOpportunities(opportunities);
        db.TrackRecommendationHistory(opportunities, now);

        var wallet = new WalletSummary(walletBalance, 0, characterOrders.Where(x => x.IsBuyOrder).Sum(x => x.Price * x.VolumeRemain), characterOrders.Where(x => !x.IsBuyOrder).Sum(x => x.Price * x.VolumeRemain));
        var typeNames = await ResolveTypeNamesAsync(esi, opportunities.Select(x => x.TypeId).Distinct().ToList());
        var performance = db.GetPerformanceSnapshot();

        var snapshot = new RadarSnapshot(
            now,
            wallet,
            characterOrders,
            marketOrders,
            opportunities,
            typeNames,
            performance,
            ["Advisory only. No in-client automation.", "Opportunities limited to Amarr hub station orders."]);

        var mdPath = Path.Combine(_root, "out", "today.md");
        var htmlPath = Path.Combine(_root, "out", "today.html");
        await File.WriteAllTextAsync(mdPath, ReportRenderer.ToMarkdown(snapshot));
        await File.WriteAllTextAsync(htmlPath, ReportRenderer.ToHtml(snapshot));

        foreach (var opportunity in opportunities)
        {
            if (!dedupeAlerts || db.TryMarkAlertSent(opportunity.Fingerprint))
            {
                var typeName = typeNames.GetValueOrDefault(opportunity.TypeId) ?? "Unknown";
                _logger.LogInformation(
                    "ALERT {Kind} item={TypeName} ({TypeId}) margin={Margin}% profit={Profit} volume={DailyVolume} confidence={Confidence}",
                    opportunity.Kind,
                    typeName,
                    opportunity.TypeId,
                    opportunity.NetMarginPct,
                    opportunity.EstimatedProfitIsk,
                    opportunity.DailyVolume,
                    opportunity.Confidence);
                await PostWebhookAsync(config.WebhookUrl, opportunity);
            }
        }
    }

    private async Task<Dictionary<int, string>> ResolveTypeNamesAsync(IEsiClient esi, IReadOnlyList<int> typeIds)
    {
        var typeNames = new Dictionary<int, string>();
        foreach (var typeId in typeIds)
        {
            var resolved = await SafeCall(() => esi.GetTypeNameAsync(typeId, CancellationToken.None), string.Empty, $"type name for {typeId}");
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                typeNames[typeId] = resolved;
            }
        }

        return typeNames;
    }

    private async Task PostWebhookAsync(string? webhookUrl, Opportunity opportunity)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        try
        {
            using var client = new HttpClient();
            var payload = JsonSerializer.Serialize(new WebhookOpportunity(opportunity.Kind, opportunity.TypeId, opportunity.Title, opportunity.NetMarginPct, opportunity.EstimatedProfitIsk, opportunity.DailyVolume, opportunity.Confidence, opportunity.DetectedAt));
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(webhookUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook endpoint returned status code {StatusCode}.", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post webhook alert.");
        }
    }

    private AppConfig LoadConfig()
    {
        var path = Path.Combine(_root, "config", "config.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Missing config/config.json. Run 'magent init' first.");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json) ?? throw new InvalidOperationException("Invalid config json.");
        ValidateConfig(config);
        return config;
    }

    private SqliteStore CreateStore() => new(Path.Combine(_root, "data", "magent.db"));

    private IEsiClient CreateEsiClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return new EsiClient(client, loggerFactory.CreateLogger<EsiClient>());
    }

    private async Task<T> SafeCall<T>(Func<Task<T>> action, T fallback, string operation)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch {Operation}. Using fallback.", operation);
            return fallback;
        }
    }

    private static void ValidateConfig(AppConfig config)
    {
        if (config.PollIntervalMinutes <= 0)
        {
            throw new InvalidOperationException("PollIntervalMinutes must be greater than 0.");
        }

        if (config.MaxWatchlistSize <= 0)
        {
            throw new InvalidOperationException("MaxWatchlistSize must be greater than 0.");
        }

        if (config.BrokerFeePct < 0 || config.SalesTaxPct < 0 || config.MinNetMarginPct < 0)
        {
            throw new InvalidOperationException("Fee and threshold percentages cannot be negative.");
        }

        if (config.MaxIskPerItem <= 0 || config.MinOrderValue < 0)
        {
            throw new InvalidOperationException("MaxIskPerItem must be > 0 and MinOrderValue cannot be negative.");
        }

        if (config.MaxPortfolioExposurePct <= 0 || config.MaxPortfolioExposurePct > 100)
        {
            throw new InvalidOperationException("MaxPortfolioExposurePct must be in (0, 100].");
        }

        if (config.MaxOrdersPerCycle <= 0)
        {
            throw new InvalidOperationException("MaxOrdersPerCycle must be greater than 0.");
        }

        if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            if (!Uri.TryCreate(config.WebhookUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("https" or "http"))
            {
                throw new InvalidOperationException("WebhookUrl must be a valid absolute HTTP(S) URL.");
            }
        }
    }

    private sealed record WebhookOpportunity(
        [property: JsonPropertyName("kind")] OpportunityKind Kind,
        [property: JsonPropertyName("typeId")] int TypeId,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("netMarginPct")] decimal NetMarginPct,
        [property: JsonPropertyName("estimatedProfitIsk")] decimal EstimatedProfitIsk,
        [property: JsonPropertyName("dailyVolume")] long DailyVolume,
        [property: JsonPropertyName("confidence")] ConfidenceLevel Confidence,
        [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt);

    private sealed record WatchlistUpdateRequest([property: JsonPropertyName("typeId")] int TypeId);

    private sealed record ExportRequest(
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("typeIds")] IReadOnlyList<int> TypeIds,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("quantityPerItem")] int? QuantityPerItem);

    private const string WebDashboardHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Magent Live Dashboard</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 24px; background: #0f172a; color: #e2e8f0; }
    h1,h2 { margin: 0 0 12px; }
    .panel { background: #111827; border: 1px solid #334155; border-radius: 8px; padding: 16px; margin-bottom: 16px; }
    input, select, button { padding: 8px; border-radius: 6px; border: 1px solid #475569; background: #1f2937; color: #e2e8f0; }
    button { cursor: pointer; margin-left: 6px; }
    table { width: 100%; border-collapse: collapse; }
    th, td { border-bottom: 1px solid #334155; padding: 8px; text-align: left; }
    th.sortable { cursor: pointer; }
    .row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .row label { font-size: 12px; color: #94a3b8; }
    .kind { font-weight: 700; }
    .confidence-high { color: #22c55e; }
    .confidence-medium { color: #f59e0b; }
    .confidence-low { color: #ef4444; }
    .meta { color: #94a3b8; font-size: 12px; margin-top: 8px; }
    .pill { border: 1px solid #334155; border-radius: 999px; padding: 3px 8px; font-size: 12px; }
  </style>
</head>
<body>
  <h1>Magent Live Dashboard</h1>
  <div class="panel">
    <h2>Watchlist</h2>
    <div class="row">
      <input id="typeIdInput" type="number" placeholder="Type ID" />
      <button onclick="addWatch()">Add</button>
    </div>
    <div class="meta">Type IDs are resolved to item names automatically when ESI metadata is available.</div>
    <ul id="watchlist"></ul>
  </div>
  <div class="panel">
    <h2>Opportunities</h2>
    <div class="row">
      <button onclick="copyOrders('buy')">Copy Buy Orders</button>
      <button onclick="copyOrders('sell')">Copy Sell Orders</button>
      <button onclick="copyOrders('buy', 'eve-multibuy')">Copy EVE Multi-Buy</button>
      <label>Qty <input id="qtyInput" type="number" value="1" min="1" style="width:72px" /></label>
      <input id="searchInput" placeholder="Filter by name / type id" oninput="applyFilters()" />
      <select id="kindFilter" onchange="applyFilters()"><option value="">All kinds</option><option value="SEED">SEED</option><option value="UPDATE">UPDATE</option><option value="FLIP">FLIP</option></select>
      <select id="confidenceFilter" onchange="applyFilters()"><option value="">All confidence</option><option value="High">High</option><option value="Medium">Medium</option><option value="Low">Low</option></select>
    </div>
    <div class="meta"><span class="pill">Click table headers to sort</span> <span class="pill">Checked rows export only selected items</span></div>
    <table>
      <thead><tr><th></th><th class="sortable" onclick="setSort('typeName')">Item</th><th class="sortable" onclick="setSort('kind')">Kind</th><th class="sortable" onclick="setSort('netMarginPct')">Margin %</th><th class="sortable" onclick="setSort('estimatedProfitIsk')">Profit ISK</th><th class="sortable" onclick="setSort('confidence')">Confidence</th><th class="sortable" onclick="setSort('bestBuy')">Best Buy</th><th class="sortable" onclick="setSort('bestSell')">Best Sell</th><th class="sortable" onclick="setSort('spread')">Spread</th></tr></thead>
      <tbody id="opps"></tbody>
    </table>
    <div id="oppsSummary" class="meta"></div>
  </div>

<script>
let state = { opportunities: [], sortKey: 'estimatedProfitIsk', sortAsc: false };

async function refresh() {
  const data = await fetch('/api/dashboard').then(r => r.json());
  const ul = document.getElementById('watchlist');
  ul.innerHTML = '';
  data.watchlist.forEach(w => {
    const li = document.createElement('li');
    li.innerHTML = `<strong>${w.typeName}</strong> <span class='meta'>(${w.typeId})</span> <button onclick="removeWatch(${w.typeId})">Remove</button>`;
    ul.appendChild(li);
  });

  state.opportunities = data.opportunities;
  applyFilters();
}

function applyFilters() {
  const tbody = document.getElementById('opps');
  tbody.innerHTML = '';
  const search = document.getElementById('searchInput').value.trim().toLowerCase();
  const kind = document.getElementById('kindFilter').value;
  const confidence = document.getElementById('confidenceFilter').value;

  const filtered = state.opportunities
    .filter(o => !kind || o.kind === kind)
    .filter(o => !confidence || o.confidence === confidence)
    .filter(o => !search || o.typeName.toLowerCase().includes(search) || String(o.typeId).includes(search));

  const sorted = [...filtered].sort((a, b) => {
    const k = state.sortKey;
    const dir = state.sortAsc ? 1 : -1;
    const av = a[k];
    const bv = b[k];
    if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * dir;
    return String(av).localeCompare(String(bv)) * dir;
  });

  sorted.forEach(o => {
    const confidenceClass = `confidence-${o.confidence.toLowerCase()}`;
    const tr = document.createElement('tr');
    tr.innerHTML = `<td><input type='checkbox' class='sel' value='${o.typeId}'></td><td><strong>${o.typeName}</strong><div class='meta'>${o.typeId}</div></td><td class='kind'>${o.kind}</td><td>${Number(o.netMarginPct).toFixed(2)}</td><td>${Number(o.estimatedProfitIsk).toLocaleString()}</td><td class='${confidenceClass}'>${o.confidence}</td><td>${Number(o.bestBuy).toFixed(2)}</td><td>${Number(o.bestSell).toFixed(2)}</td><td>${Number(o.spread).toFixed(2)}</td>`;
    tbody.appendChild(tr);
  });

  document.getElementById('oppsSummary').textContent = `${sorted.length} shown / ${state.opportunities.length} total opportunities`;
}

function setSort(key) {
  if (state.sortKey === key) {
    state.sortAsc = !state.sortAsc;
  } else {
    state.sortKey = key;
    state.sortAsc = false;
  }
  applyFilters();
}

async function addWatch() {
  const typeId = Number(document.getElementById('typeIdInput').value);
  await fetch('/api/watchlist', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ typeId }) });
  await refresh();
}

async function removeWatch(typeId) {
  await fetch(`/api/watchlist/${typeId}`, { method: 'DELETE' });
  await refresh();
}

async function copyOrders(side, format = 'tabular') {
  let typeIds = Array.from(document.querySelectorAll('.sel:checked')).map(x => Number(x.value));
  if (typeIds.length === 0) {
    typeIds = Array.from(document.querySelectorAll('#opps .sel')).map(x => Number(x.value));
  }
  const quantityPerItem = Number(document.getElementById('qtyInput').value) || 1;
  const payload = await fetch('/api/export', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ side, typeIds, format, quantityPerItem }) }).then(r => r.json());
  await navigator.clipboard.writeText(payload.clipboard || '');
  alert('Copied to clipboard');
}

refresh();
setInterval(refresh, 15000);
</script>
</body>
</html>
""";

    private sealed class TokenStore(ILogger<TokenStore> logger)
    {
        private readonly string _tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".magent", "refresh_token.txt");

        public async Task SaveRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_tokenPath)!);
            await File.WriteAllTextAsync(_tokenPath, refreshToken.Trim(), cancellationToken);

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Unable to enforce strict permissions on refresh token file.");
                }
            }
        }

        public async Task<string?> LoadRefreshTokenAsync(CancellationToken cancellationToken)
        {
            if (!File.Exists(_tokenPath))
            {
                return null;
            }

            return (await File.ReadAllTextAsync(_tokenPath, cancellationToken)).Trim();
        }
    }
}
