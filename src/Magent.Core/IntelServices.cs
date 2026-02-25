using System.Text.RegularExpressions;

namespace Magent.Core;

public sealed class LocalChatParser
{
    private static readonly Regex SpeakerRegex = new(@"\]\s+([^>]+)\s+>", RegexOptions.Compiled);

    public IReadOnlyList<string> ExtractPilotNames(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];
        var match = SpeakerRegex.Match(line);
        if (match.Success)
        {
            return [Normalize(match.Groups[1].Value)];
        }

        if (line.Contains("Listener:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
            {
                return parts[1].Split(',').Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        return [];
    }

    public string? TryExtractSystemName(string line)
    {
        const string marker = "Channel Name: Local :";
        var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        return Normalize(line[(idx + marker.Length)..]);
    }

    private static string Normalize(string value) => value.Trim().Trim('"');
}

public sealed class PilotThreatScorer
{
    public PilotThreatScoreResult Score(PilotIntel intel, IReadOnlyList<DenylistEntry> denylist, DateTimeOffset now)
    {
        var score = 0;
        var reasons = new List<string>();

        var kills = intel.RecentKills7d > 0 ? intel.RecentKills7d : intel.RecentKills30d;
        if (kills >= 30) { score += 30; reasons.Add($"Heavy recent PvP activity ({kills} kills)."); }
        else if (kills >= 10) { score += 18; reasons.Add($"Moderate recent PvP activity ({kills} kills)."); }
        else if (kills > 0) { score += 8; reasons.Add($"Some recent PvP activity ({kills} kills)."); }
        else reasons.Add("Recent PvP activity unknown or none.");

        if (intel.LowsecKillRatio is { } lowsec)
        {
            if (lowsec >= 0.7) { score += 18; reasons.Add("Kills are mostly in lowsec."); }
            else if (lowsec >= 0.4) { score += 10; reasons.Add("Notable lowsec activity share."); }
        }
        else reasons.Add("Lowsec focus ratio unknown.");

        if (intel.LastPvpAt is { } last)
        {
            var age = now - last;
            if (age <= TimeSpan.FromHours(2)) { score += 20; reasons.Add("PvP activity within last 2h."); }
            else if (age <= TimeSpan.FromHours(24)) { score += 10; reasons.Add("PvP activity within last 24h."); }
        }
        else reasons.Add("Recent activity recency unknown.");

        if (intel.HunterShipSeen)
        {
            score += 12;
            reasons.Add($"Hunter ships detected ({string.Join(", ", intel.HunterShips.Take(3))}).");
        }

        if (intel.SecurityStatus is { } sec)
        {
            if (sec < -5) { score += 10; reasons.Add($"Very low security status ({sec:0.0})."); }
            else if (sec < -2) { score += 6; reasons.Add($"Low security status ({sec:0.0})."); }
        }
        else reasons.Add("Security status unknown.");

        foreach (var entry in denylist)
        {
            if (MatchesDenylist(entry, intel.Name, intel.CorporationName, intel.AllianceName))
            {
                score += Math.Max(1, entry.Weight);
                reasons.Add($"Denylist match: {entry.Type} {entry.Pattern}.");
            }
        }

        score = Math.Clamp(score, 0, 100);
        return new PilotThreatScoreResult(intel.Name, score, ThreatBanding.FromScore(score), reasons, intel);
    }

    private static bool MatchesDenylist(DenylistEntry entry, string name, string? corp, string? alliance)
    {
        var target = entry.Type.ToLowerInvariant() switch
        {
            "corp" => corp,
            "alliance" => alliance,
            _ => name
        };

        return !string.IsNullOrWhiteSpace(target)
            && target.Contains(entry.Pattern, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SystemDangerScorer
{
    public SystemDangerScoreResult Score(SystemIntel intel, IReadOnlyList<PilotThreatScoreResult> pilots, IReadOnlyList<DenylistEntry> denylist)
    {
        var score = 0;
        var reasons = new List<string>();

        if (intel.ShipKillsLastHour > 0)
        {
            var points = Math.Min(30, intel.ShipKillsLastHour * 3);
            score += points;
            reasons.Add($"Recent ship kills in system: {intel.ShipKillsLastHour}.");
        }

        if (intel.PodKillsLastHour > 0)
        {
            var points = Math.Min(20, intel.PodKillsLastHour * 4);
            score += points;
            reasons.Add($"Recent pod kills in system: {intel.PodKillsLastHour}.");
        }

        if (intel.JumpsLastHour > 50)
        {
            score += 10;
            reasons.Add($"High traffic ({intel.JumpsLastHour} jumps/h).");
        }

        var highCount = pilots.Count(x => x.Band == ThreatBand.High);
        var extremeCount = pilots.Count(x => x.Band == ThreatBand.Extreme);
        score += highCount * 10 + extremeCount * 20;
        if (highCount + extremeCount > 0)
        {
            reasons.Add($"Dangerous pilots in local: {highCount} HIGH, {extremeCount} EXTREME.");
        }

        var denyMatches = pilots.Count(p => denylist.Any(d => p.Reasons.Any(r => r.Contains("Denylist", StringComparison.OrdinalIgnoreCase))));
        if (denyMatches > 0)
        {
            score += Math.Min(20, denyMatches * 5);
            reasons.Add($"Denylisted entities present ({denyMatches}).");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Limited intel available; treating as low baseline risk.");
        }

        score = Math.Clamp(score, 0, 100);
        return new SystemDangerScoreResult(score, ThreatBanding.FromScore(score), reasons, intel);
    }
}

public sealed class AlertCooldownGate
{
    private readonly Dictionary<string, (ThreatBand Band, DateTimeOffset At)> _state = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldAlert(string key, ThreatBand band, DateTimeOffset now, TimeSpan cooldown)
    {
        if (!_state.TryGetValue(key, out var last))
        {
            _state[key] = (band, now);
            return true;
        }

        if (band > last.Band || now - last.At >= cooldown)
        {
            _state[key] = (band, now);
            return true;
        }

        return false;
    }
}
