using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Magent.Core;
using Microsoft.Extensions.Logging;

namespace Magent.Esi;

public sealed class EsiClient(HttpClient httpClient, ILogger<EsiClient> logger) : IEsiClient
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EsiAuthResult> AuthorizeAsync(Uri callbackBaseUri, CancellationToken cancellationToken)
    {
        // MVP placeholder for device/manual flow bootstrap.
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".magent", "refresh_token.txt");
        if (!File.Exists(file))
        {
            throw new InvalidOperationException($"No token found. Put a refresh token in {file} after completing ESI OAuth.");
        }

        var token = (await File.ReadAllTextAsync(file, cancellationToken)).Trim();
        return new EsiAuthResult("Unknown", 0, token, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<CharacterOrder>> GetCharacterOrdersAsync(string refreshToken, CancellationToken cancellationToken)
    {
        // Stub endpoint shape; token never logged.
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/latest/characters/0/orders/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);
        var response = await SendWithRetryAsync(req, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var raw = JsonSerializer.Deserialize<List<EsiOrder>>(payload, _jsonOptions) ?? [];
            return raw.Select(x => new CharacterOrder(x.order_id, x.type_id, x.is_buy_order, x.price, x.volume_remain, x.location_id, x.issued, x.issued.AddDays(x.duration))).ToList();
        }
        catch
        {
            logger.LogWarning("Failed to parse character orders; returning empty dataset.");
            return [];
        }
    }

    public async Task<IReadOnlyList<MarketOrder>> GetMarketOrdersAsync(int regionId, int typeId, string? etag, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/latest/markets/{regionId}/orders/?order_type=all&type_id={typeId}");
        if (!string.IsNullOrWhiteSpace(etag))
        {
            req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        }

        var response = await SendWithRetryAsync(req, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return [];
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var raw = JsonSerializer.Deserialize<List<EsiOrder>>(payload, _jsonOptions) ?? [];
        return raw.Select(x => new MarketOrder(x.order_id, x.type_id, x.is_buy_order, x.price, x.volume_remain, x.location_id, x.issued, x.issued.AddDays(x.duration))).ToList();
    }

    public async Task<IReadOnlyList<MarketHistoryPoint>> GetMarketHistoryAsync(int regionId, int typeId, string? etag, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/latest/markets/{regionId}/history/?type_id={typeId}");
        if (!string.IsNullOrWhiteSpace(etag))
        {
            req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        }

        var response = await SendWithRetryAsync(req, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return [];
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var raw = JsonSerializer.Deserialize<List<EsiHistory>>(payload, _jsonOptions) ?? [];
        return raw.Select(x => new MarketHistoryPoint(typeId, DateOnly.FromDateTime(x.date), x.volume, x.average)).ToList();
    }

    public async Task<decimal> GetWalletBalanceAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/latest/characters/0/wallet/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshToken);
        var response = await SendWithRetryAsync(req, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return decimal.TryParse(payload, out var result) ? result : 0;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage req, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var clone = await CloneAsync(req, cancellationToken);
            var response = await httpClient.SendAsync(clone, cancellationToken);
            if ((int)response.StatusCode < 500 && response.StatusCode != (HttpStatusCode)429)
            {
                return response;
            }

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            logger.LogWarning("ESI transient error {StatusCode}, retrying in {DelaySeconds}s", (int)response.StatusCode, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }

        logger.LogWarning("ESI request exhausted retries; returning synthetic 503 response.");
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        };
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage req, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri);
        foreach (var header in req.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (req.Content is not null)
        {
            var bytes = await req.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in req.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        return clone;
    }

    private sealed record EsiOrder(long order_id, int type_id, bool is_buy_order, decimal price, int volume_remain, long location_id, int duration, DateTimeOffset issued);
    private sealed record EsiHistory(DateTime date, long volume, decimal average);
}
