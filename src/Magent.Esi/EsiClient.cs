using System.Globalization;
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
    private AccessTokenState? _cachedToken;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public async Task<EsiAuthResult> AuthorizeAsync(Uri callbackBaseUri, CancellationToken cancellationToken)
    {
        Console.Write("Paste ESI refresh token: ");
        var refreshToken = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Refresh token cannot be empty.");
        }

        var verify = await VerifyCharacterAsync(refreshToken, cancellationToken);
        return new EsiAuthResult(verify.CharacterName, verify.CharacterId, refreshToken, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<CharacterOrder>> GetCharacterOrdersAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var verify = await VerifyCharacterAsync(refreshToken, cancellationToken);
        using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, $"https://esi.evetech.net/latest/characters/{verify.CharacterId}/orders/", refreshToken, cancellationToken);
        using var response = await SendWithRetryAsync(req, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return [];
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var raw = JsonSerializer.Deserialize<List<EsiOrder>>(payload, _jsonOptions) ?? [];
            return raw.Select(x => new CharacterOrder(x.order_id, x.type_id, x.is_buy_order, x.price, x.volume_remain, x.location_id, x.issued, x.issued.AddDays(x.duration))).ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse character orders; returning empty dataset.");
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

        using var response = await SendWithRetryAsync(req, cancellationToken);
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

        using var response = await SendWithRetryAsync(req, cancellationToken);
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
        var verify = await VerifyCharacterAsync(refreshToken, cancellationToken);
        using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, $"https://esi.evetech.net/latest/characters/{verify.CharacterId}/wallet/", refreshToken, cancellationToken);
        using var response = await SendWithRetryAsync(req, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return decimal.TryParse(payload, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private async Task<VerifiedCharacter> VerifyCharacterAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, "https://login.eveonline.com/oauth/verify", refreshToken, cancellationToken);
        using var response = await SendWithRetryAsync(req, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var verify = JsonSerializer.Deserialize<EsiVerifyResponse>(payload, _jsonOptions)
                     ?? throw new InvalidOperationException("Unable to parse ESI verify response.");
        return new VerifiedCharacter(verify.CharacterID, verify.CharacterName ?? "Unknown");
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(HttpMethod method, string uri, string refreshToken, CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(refreshToken, cancellationToken);
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    private async Task<string> GetAccessTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is not null && _cachedToken.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) && _cachedToken.RefreshToken == refreshToken)
            {
                return _cachedToken.AccessToken;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://login.eveonline.com/v2/oauth/token");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            });

            var clientId = Environment.GetEnvironmentVariable("MAGENT_ESI_CLIENT_ID");
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new InvalidOperationException("MAGENT_ESI_CLIENT_ID is required to exchange refresh tokens for access tokens.");
            }

            req.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:")));

            using var response = await SendWithRetryAsync(req, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var token = JsonSerializer.Deserialize<EsiTokenResponse>(payload, _jsonOptions)
                        ?? throw new InvalidOperationException("Unable to parse ESI token response.");

            _cachedToken = new AccessTokenState(token.access_token, DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, token.expires_in)), refreshToken);
            return _cachedToken.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
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

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            logger.LogWarning("ESI transient error {StatusCode}, retrying in {DelaySeconds}s", (int)response.StatusCode, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }

        throw new HttpRequestException("ESI request failed after retries.");
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

    private sealed record AccessTokenState(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken);
    private sealed record VerifiedCharacter(long CharacterId, string CharacterName);
    private sealed record EsiOrder(long order_id, int type_id, bool is_buy_order, decimal price, int volume_remain, long location_id, int duration, DateTimeOffset issued);
    private sealed record EsiHistory(DateTime date, long volume, decimal average);
    private sealed record EsiTokenResponse(string access_token, int expires_in, string token_type);
    private sealed record EsiVerifyResponse(long CharacterID, string CharacterName);
}
