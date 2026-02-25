using Magent.Core;

namespace Magent.Esi;

public interface IEsiClient
{
    Task<EsiAuthResult> AuthorizeAsync(Uri callbackBaseUri, CancellationToken cancellationToken);
    Task<IReadOnlyList<CharacterOrder>> GetCharacterOrdersAsync(string refreshToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketOrder>> GetMarketOrdersAsync(int regionId, int typeId, string? etag, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketHistoryPoint>> GetMarketHistoryAsync(int regionId, int typeId, string? etag, CancellationToken cancellationToken);
    Task<decimal> GetWalletBalanceAsync(string refreshToken, CancellationToken cancellationToken);
    Task<string?> GetTypeNameAsync(int typeId, CancellationToken cancellationToken);
    Task<long?> ResolveCharacterIdAsync(string name, CancellationToken cancellationToken);
    Task<EsiCharacterPublic?> GetCharacterPublicAsync(long characterId, CancellationToken cancellationToken);
    Task<string?> GetCorporationNameAsync(long corporationId, CancellationToken cancellationToken);
    Task<string?> GetAllianceNameAsync(long allianceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EsiSystemKillStats>> GetSystemKillsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<EsiSystemJumpStats>> GetSystemJumpsAsync(CancellationToken cancellationToken);
}
