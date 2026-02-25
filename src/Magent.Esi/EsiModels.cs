namespace Magent.Esi;

public sealed record EsiAuthResult(string CharacterName, long CharacterId, string RefreshToken, DateTimeOffset ReceivedAt);

public sealed record EsiCharacterPublic(long CharacterId, long CorporationId, long? AllianceId, double? SecurityStatus);
public sealed record EsiSystemKillStats(int system_id, int ship_kills, int pod_kills);
public sealed record EsiSystemJumpStats(int system_id, int ship_jumps);
