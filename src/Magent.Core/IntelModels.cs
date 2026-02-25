namespace Magent.Core;

public enum ThreatBand
{
    Low,
    Med,
    High,
    Extreme
}

public sealed record IntelConfig(
    string? ChatlogPath = null,
    int PilotScoreTtlMinutes = 10,
    int SystemScoreTtlSeconds = 120,
    int AlertCooldownSeconds = 300,
    ThreatBand SystemAlertThresholdBand = ThreatBand.Med,
    IReadOnlyList<DenylistEntry>? Denylist = null,
    string? WebhookUrl = null,
    int HighPilotWeight = 10,
    int ExtremePilotWeight = 20);

public sealed record DenylistEntry(string Type, string Pattern, int Weight, string? Note);

public sealed record PilotIntel(
    string Name,
    long? CharacterId,
    long? CorporationId,
    string? CorporationName,
    long? AllianceId,
    string? AllianceName,
    double? SecurityStatus,
    int RecentKills7d,
    int RecentKills30d,
    double? LowsecKillRatio,
    DateTimeOffset? LastPvpAt,
    bool HunterShipSeen,
    IReadOnlyList<string> HunterShips,
    IReadOnlyList<string> Notes);

public sealed record PilotThreatScoreResult(string Name, int Score, ThreatBand Band, IReadOnlyList<string> Reasons, PilotIntel Intel);

public sealed record SystemIntel(
    int? SystemId,
    string? SystemName,
    int JumpsLastHour,
    int ShipKillsLastHour,
    int PodKillsLastHour,
    IReadOnlyList<string> Notes);

public sealed record SystemDangerScoreResult(int Score, ThreatBand Band, IReadOnlyList<string> Reasons, SystemIntel Intel);

public interface IPilotIntelSource
{
    Task<PilotIntel> ResolvePilotAsync(string name, CancellationToken cancellationToken);
}

public interface ISystemIntelSource
{
    Task<SystemIntel> ResolveSystemAsync(int? systemId, string? systemName, CancellationToken cancellationToken);
}

public static class ThreatBanding
{
    public static ThreatBand FromScore(int score) => score switch
    {
        >= 80 => ThreatBand.Extreme,
        >= 60 => ThreatBand.High,
        >= 30 => ThreatBand.Med,
        _ => ThreatBand.Low
    };
}
