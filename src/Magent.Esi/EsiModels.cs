namespace Magent.Esi;

public sealed record EsiAuthResult(string CharacterName, long CharacterId, string RefreshToken, DateTimeOffset ReceivedAt);
