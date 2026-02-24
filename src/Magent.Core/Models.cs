namespace Magent.Core;

public enum OpportunityKind
{
    Update,
    Seed,
    Flip
}

public enum ConfidenceLevel
{
    Low,
    Medium,
    High
}

public sealed record AppConfig(
    int RegionId,
    long HubLocationId,
    int PollIntervalMinutes,
    decimal BrokerFeePct,
    decimal SalesTaxPct,
    decimal MinNetMarginPct,
    int MinDailyVolume,
    int MaxWatchlistSize,
    string? WebhookUrl);

public sealed record CharacterOrder(
    long OrderId,
    int TypeId,
    bool IsBuyOrder,
    decimal Price,
    int VolumeRemain,
    long LocationId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record MarketOrder(
    long OrderId,
    int TypeId,
    bool IsBuyOrder,
    decimal Price,
    int VolumeRemain,
    long LocationId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record MarketHistoryPoint(
    int TypeId,
    DateOnly Date,
    long Volume,
    decimal AveragePrice);

public sealed record WalletSummary(decimal Balance, decimal Escrow, decimal BuyOrderValue, decimal SellOrderValue);

public sealed record Opportunity(
    OpportunityKind Kind,
    int TypeId,
    string Title,
    string Notes,
    decimal NetMarginPct,
    decimal EstimatedProfitIsk,
    long DailyVolume,
    ConfidenceLevel Confidence,
    DateTimeOffset DetectedAt,
    string Fingerprint);

public sealed record RadarSnapshot(
    DateTimeOffset Timestamp,
    WalletSummary Wallet,
    IReadOnlyList<CharacterOrder> CharacterOrders,
    IReadOnlyList<MarketOrder> MarketOrders,
    IReadOnlyList<Opportunity> Opportunities,
    IReadOnlyList<string> RiskNotes);
