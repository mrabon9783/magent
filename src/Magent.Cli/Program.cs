using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magent.Core;
using Magent.Data;
using Magent.Esi;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
}));
var logger = loggerFactory.CreateLogger("magent");

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
var app = new MagentApp(loggerFactory);

return command switch
{
    "init" => await app.InitAsync(),
    "auth" => await app.AuthAsync(),
    "sync" => await app.SyncAsync(),
    "run" => await app.RunAsync(),
    "report" => await app.ReportAsync(),
    _ => PrintHelp(logger)
};

static int PrintHelp(ILogger logger)
{
    logger.LogInformation("Commands: magent init | auth | sync | run | report");
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
            var config = new AppConfig(10000043, 60008494, 15, 3m, 4.5m, 2m, 50, 250, null);
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

        var calculator = new OpportunityCalculator();
        var opportunities = calculator.Calculate(config, characterOrders, marketOrders, dailyVolumes, DateTimeOffset.UtcNow);
        db.SaveOpportunities(opportunities);

        var token = await _tokenStore.LoadRefreshTokenAsync(CancellationToken.None);
        var walletBalance = string.IsNullOrWhiteSpace(token)
            ? 0
            : await SafeCall(() => esi.GetWalletBalanceAsync(token, CancellationToken.None), 0m, "wallet balance");
        var wallet = new WalletSummary(walletBalance, 0, characterOrders.Where(x => x.IsBuyOrder).Sum(x => x.Price * x.VolumeRemain), characterOrders.Where(x => !x.IsBuyOrder).Sum(x => x.Price * x.VolumeRemain));

        var snapshot = new RadarSnapshot(
            DateTimeOffset.UtcNow,
            wallet,
            characterOrders,
            marketOrders,
            opportunities,
            ["Advisory only. No in-client automation.", "Opportunities limited to Amarr hub station orders."]);

        var mdPath = Path.Combine(_root, "out", "today.md");
        var htmlPath = Path.Combine(_root, "out", "today.html");
        await File.WriteAllTextAsync(mdPath, ReportRenderer.ToMarkdown(snapshot));
        await File.WriteAllTextAsync(htmlPath, ReportRenderer.ToHtml(snapshot));

        foreach (var opportunity in opportunities)
        {
            if (!dedupeAlerts || db.TryMarkAlertSent(opportunity.Fingerprint))
            {
                _logger.LogInformation("ALERT {Kind} type={TypeId} margin={Margin}% profit={Profit}", opportunity.Kind, opportunity.TypeId, opportunity.NetMarginPct, opportunity.EstimatedProfitIsk);
                await PostWebhookAsync(config.WebhookUrl, opportunity);
            }
        }
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
