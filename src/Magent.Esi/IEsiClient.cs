using Magent.Core;

namespace Magent.Esi;

public interface IEsiClient
{
    Task<EsiAuthResult> AuthorizeAsync(Uri callbackBaseUri, CancellationToken cancellationToken);
    Task<IReadOnlyList<CharacterOrder>> GetCharacterOrdersAsync(string refreshToken, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketOrder>> GetMarketOrdersAsync(int regionId, int typeId, string? etag, CancellationToken cancellationToken);
    Task<IReadOnlyList<MarketHistoryPoint>> GetMarketHistoryAsync(int regionId, int typeId, string? etag, CancellationToken cancellationToken);
    Task<decimal> GetWalletBalanceAsync(string refreshToken, CancellationToken cancellationToken);
}
