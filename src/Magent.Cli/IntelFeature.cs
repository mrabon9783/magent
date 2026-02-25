using System.Text;
using System.Text.Json;
using Magent.Core;
using Magent.Data;
using Magent.Esi;
using Microsoft.Extensions.Logging;

namespace Magent.Cli;

internal sealed class IntelRunner(ILogger logger, SqliteStore db, IEsiClient esi, string root)
{
    private readonly LocalChatParser _parser = new();
    private readonly PilotThreatScorer _pilotScorer = new();
    private readonly SystemDangerScorer _systemScorer = new();

    public async Task<int> PasteAsync(AppConfig appConfig, IReadOnlyList<string> names, string? systemName, CancellationToken ct)
    {
        var intelConfig = appConfig.Intel ?? new IntelConfig();
        var denylist = intelConfig.Denylist ?? [];
        var pilotSource = new CompositePilotIntelSource(esi, logger);
        var systemSource = new EsiSystemIntelSource(esi, logger);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, PilotThreatScoreResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await ResolveAndPrintPilotAsync(n, pilotSource, denylist, intelConfig, seen, scores, ct);
        }

        logger.LogInformation("Paste mode active. Monitoring clipboard for pilot names (Ctrl+C to stop).");
        var lastClipboardText = string.Empty;

        try
        {
            while (!ct.IsCancellationRequested)
            {
            var clipboardText = await TryReadClipboardTextAsync(ct);
            var changed = false;
            if (!string.IsNullOrWhiteSpace(clipboardText) && !string.Equals(lastClipboardText, clipboardText, StringComparison.Ordinal))
            {
                foreach (var pilot in ExtractPotentialPilotNames(clipboardText))
                {
                    changed |= await ResolveAndPrintPilotAsync(pilot, pilotSource, denylist, intelConfig, seen, scores, ct);
                }

                lastClipboardText = clipboardText;
            }

            if (changed)
            {
                //await RefreshSystemSummaryAsync(systemSource, scores.Values.ToList(), denylist, systemName, writeReport: true, printSummary: true, ct);
            }

                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Paste monitoring stopped.");
        }

        return 0;
    }

    private async Task<bool> ResolveAndPrintPilotAsync(
        string pilot,
        IPilotIntelSource pilotSource,
        IReadOnlyList<DenylistEntry> denylist,
        IntelConfig intelConfig,
        HashSet<string> seen,
        Dictionary<string, PilotThreatScoreResult> scores,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pilot) || !seen.Add(pilot))
        {
            return false;
        }

        //logger.LogInformation("NEW IN LOCAL: {Pilot} (intel pending)", pilot);
        var resolved = await ResolvePilotScoreAsync(pilot, pilotSource, denylist, intelConfig, ct);
        scores[pilot] = resolved;
        PrintPilot(resolved);
        return true;
    }

    private async Task RefreshSystemSummaryAsync(
        ISystemIntelSource systemSource,
        IReadOnlyList<PilotThreatScoreResult> scores,
        IReadOnlyList<DenylistEntry> denylist,
        string? systemName,
        bool writeReport,
        bool printSummary,
        CancellationToken ct)
    {
        if (scores.Count == 0)
        {
            return;
        }

        var sysIntel = await systemSource.ResolveSystemAsync(null, systemName, ct);
        var sysScore = _systemScorer.Score(sysIntel, scores, denylist);
        PrintSystem(sysScore, sysIntel.SystemName ?? "Unknown");
        if (writeReport)
        {
            WriteIntelReport(scores, sysScore, sysIntel.SystemName);
        }

        if (printSummary)
        {
            PrintSummary(scores, sysIntel.SystemName ?? "Unknown");
        }
    }

    public async Task<int> WatchAsync(AppConfig appConfig, string? chatlogPathOverride, CancellationToken ct)
    {
        var intelConfig = appConfig.Intel ?? new IntelConfig();
        var denylist = intelConfig.Denylist ?? [];
        var path = chatlogPathOverride ?? intelConfig.ChatlogPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "EVE", "logs", "Chatlogs");
        if (!Directory.Exists(path))
        {
            logger.LogWarning("Chatlog path does not exist: {Path}", path);
            return 1;
        }

        var pilotSource = new CompositePilotIntelSource(esi, logger);
        var systemSource = new EsiSystemIntelSource(esi, logger);
        var seenBySession = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, PilotThreatScoreResult>(StringComparer.OrdinalIgnoreCase);
        var positionByFile = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var lastSystemBand = ThreatBand.Low;
        logger.LogInformation("Watching {Path} for local intel.", path);

        try
        {
            while (!ct.IsCancellationRequested)
            {
            var active = SelectActiveLocalFile(path);
            if (active is null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            if (!positionByFile.TryGetValue(active, out var pos)) pos = 0;
            using var fs = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (pos > fs.Length) pos = 0;
            fs.Seek(pos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);

            string? currentSystem = Path.GetFileNameWithoutExtension(active);
            var sessionKey = currentSystem;
            if (!seenBySession.TryGetValue(sessionKey, out var seen))
            {
                seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seenBySession[sessionKey] = seen;
            }

            string? line;
            var changed = false;
            while ((line = await sr.ReadLineAsync(ct)) is not null)
            {
                var extractedSystem = _parser.TryExtractSystemName(line);
                if (!string.IsNullOrWhiteSpace(extractedSystem)) currentSystem = extractedSystem;

                foreach (var pilot in _parser.ExtractPilotNames(line))
                {
                    if (!seen.Add(pilot)) continue;
                    logger.LogInformation("NEW IN LOCAL: {PilotName}  (intel pending...)", pilot);
                    var scored = await ResolvePilotScoreAsync(pilot, pilotSource, denylist, intelConfig, ct);
                    scores[pilot] = scored;
                    PrintPilot(scored);
                    PrintSummary(scores.Values.ToList(), currentSystem ?? "Unknown");
                    changed = true;
                }
            }

            positionByFile[active] = fs.Position;

            if (changed)
            {
                var sysIntel = await systemSource.ResolveSystemAsync(null, currentSystem, ct);
                var sysScore = _systemScorer.Score(sysIntel, scores.Values.ToList(), denylist);
                if (sysScore.Band != lastSystemBand || sysScore.Score >= (int)(intelConfig.SystemAlertThresholdBand switch { ThreatBand.Low => 0, ThreatBand.Med => 30, ThreatBand.High => 60, _ => 80 }))
                {
                    PrintSystem(sysScore, currentSystem ?? "Unknown");
                    lastSystemBand = sysScore.Band;
                }

                WriteIntelReport(scores.Values.ToList(), sysScore, currentSystem);
            }

                await Task.Delay(750, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Intel watch stopped.");
        }

        return 0;
    }

    private async Task<PilotThreatScoreResult> ResolvePilotScoreAsync(string pilot, IPilotIntelSource source, IReadOnlyList<DenylistEntry> denylist, IntelConfig intelConfig, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var intel = await source.ResolvePilotAsync(pilot, ct);
        var pilotId = db.UpsertPilot(pilot, intel.CharacterId, intel.CorporationId, intel.AllianceId, now);
        var cached = db.GetPilotScoreCache(pilotId, now);
        if (cached is not null)
        {
            var reasons = JsonSerializer.Deserialize<List<string>>(cached.Value.ReasonsJson) ?? [];
            return new PilotThreatScoreResult(pilot, cached.Value.Score, Enum.Parse<ThreatBand>(cached.Value.Band, true), reasons, intel);
        }

        var scored = _pilotScorer.Score(intel, denylist, now);
        db.SavePilotScore(pilotId, scored.Score, scored.Band.ToString(), JsonSerializer.Serialize(scored.Reasons), now, now.AddMinutes(intelConfig.PilotScoreTtlMinutes));
        return scored;
    }

    private static string? SelectActiveLocalFile(string path) => Directory.EnumerateFiles(path, "*.txt")
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault(f => Path.GetFileName(f).Contains("local", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ExtractPotentialPilotNames(string clipboardText)
    {
        var separators = new[] { '\n', '\r', ',', ';', '\t' };
        return clipboardText.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> TryReadClipboardTextAsync(CancellationToken ct)
    {
        var commands = new[]
        {
            "pbpaste",
            "xclip -selection clipboard -o",
            "powershell -NoProfile -Command \"Get-Clipboard -Raw\""
        };

        foreach (var command in commands)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "cmd" : "/bin/bash",
                    Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-lc \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is null) continue;
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) return output.Trim();
            }
            catch
            {
                // Try next clipboard command.
            }
        }

        return null;
    }

    private void PrintPilot(PilotThreatScoreResult r)
    {
        var band = r.Band.ToString().ToUpperInvariant();
        var corp = r.Intel.CorporationName ?? "Unknown";
        var alliance = r.Intel.AllianceName ?? "Unknown";

        ConsoleColor originalColor = Console.ForegroundColor;

        Console.ForegroundColor = GetBandColor(r.Band);

        Console.WriteLine($"THREAT: {band} ({r.Score}/100)  {r.Name}");

        Console.ForegroundColor = originalColor;

        foreach (var reason in r.Reasons.Take(2))
            Console.WriteLine($"  - {reason}");

        Console.WriteLine($"CORP/ALLIANCE: {corp} / {alliance}");
        Console.WriteLine(); // spacing
    }

    private ConsoleColor GetBandColor(ThreatBand band)
    {
        return band switch
        {
            ThreatBand.Low => ConsoleColor.Green,
            ThreatBand.Med => ConsoleColor.Yellow,
            ThreatBand.High => ConsoleColor.Red,
            ThreatBand.Extreme => ConsoleColor.Magenta,
            _ => ConsoleColor.White
        };
    }

    private void PrintSummary(IReadOnlyList<PilotThreatScoreResult> list, string system)
    {
        logger.LogInformation("LOCAL SUMMARY (System: {System})", system);
        logger.LogInformation("  Extreme: {Extreme}  High: {High}  Med: {Med}  Low: {Low}", list.Count(x => x.Band == ThreatBand.Extreme), list.Count(x => x.Band == ThreatBand.High), list.Count(x => x.Band == ThreatBand.Med), list.Count(x => x.Band == ThreatBand.Low));
        foreach (var t in list.OrderByDescending(x => x.Score).Take(2)) logger.LogInformation("    - {Pilot} ({Band} {Score})", t.Name, t.Band.ToString().ToUpperInvariant(), t.Score);
    }

    private void PrintSystem(SystemDangerScoreResult score, string system)
    {
        logger.LogInformation("SYSTEM RISK: {Band} ({Score}/100)  {System}", score.Band.ToString().ToUpperInvariant(), score.Score, system);
        foreach (var reason in score.Reasons.Take(2)) logger.LogInformation("  - {Reason}", reason);
    }

    private void WriteIntelReport(IReadOnlyList<PilotThreatScoreResult> pilots, SystemDangerScoreResult system, string? systemName)
    {
        Directory.CreateDirectory(Path.Combine(root, "out"));
        var md = new StringBuilder();
        md.AppendLine($"# Lowsec Intel Report\n\nTimestamp: {DateTimeOffset.UtcNow:O}\n\nCurrent System: {systemName ?? "Unknown"}\n");
        md.AppendLine("## Local Summary");
        md.AppendLine($"Extreme: {pilots.Count(x => x.Band == ThreatBand.Extreme)} High: {pilots.Count(x => x.Band == ThreatBand.High)} Med: {pilots.Count(x => x.Band == ThreatBand.Med)} Low: {pilots.Count(x => x.Band == ThreatBand.Low)}\n");
        md.AppendLine("## Pilot Details");
        foreach (var p in pilots.OrderByDescending(x => x.Score))
        {
            md.AppendLine($"- **{p.Name}** {p.Band} ({p.Score}/100)");
            foreach (var r in p.Reasons.Take(3)) md.AppendLine($"  - {r}");
        }
        md.AppendLine("\n## System Risk");
        md.AppendLine($"**{system.Band} ({system.Score}/100)**");
        foreach (var r in system.Reasons) md.AppendLine($"- {r}");

        var mdPath = Path.Combine(root, "out", "intel_latest.md");
        var htmlPath = Path.Combine(root, "out", "intel_latest.html");
        File.WriteAllText(mdPath, md.ToString());
        File.WriteAllText(htmlPath, $"<html><body><pre>{System.Net.WebUtility.HtmlEncode(md.ToString())}</pre></body></html>");
    }
}

internal sealed class CompositePilotIntelSource(IEsiClient esi, ILogger logger) : IPilotIntelSource
{
    private static readonly HashSet<string> HunterShips = ["Astero", "Stratios", "Loki", "Proteus", "Hecate", "Sabre", "Rapier", "Arazu"];

    public async Task<PilotIntel> ResolvePilotAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var charId = await esi.ResolveCharacterIdAsync(name, cancellationToken);
            if (charId is null) return new PilotIntel(name, null, null, null, null, null, null, 0, 0, null, null, null, null, false, [], ["Character not found in ESI."]);
            var character = await esi.GetCharacterPublicAsync(charId.Value, cancellationToken);
            var corpName = character is null ? null : await esi.GetCorporationNameAsync(character.CorporationId, cancellationToken);
            var allianceName = character?.AllianceId is null ? null : await esi.GetAllianceNameAsync(character.AllianceId.Value, cancellationToken);

            var zkb = await ResolveZkillAsync(charId.Value, cancellationToken);
            return new PilotIntel(name, charId, character?.CorporationId, corpName, character?.AllianceId, allianceName, character?.SecurityStatus, zkb.Kills7d, zkb.Kills30d, zkb.ActivePvpKills, zkb.LowsecRatio, zkb.DangerRatio, zkb.GangRatio, zkb.LastPvpAt, zkb.HunterSeen, zkb.HunterShips, zkb.Notes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed pilot intel lookup for {Pilot}", name);
            return new PilotIntel(name, null, null, null, null, null, null, 0, 0, null, null, null, null, false, [], ["Pilot intel lookup failed."]);
        }
    }

    private static async Task<(int Kills7d, int Kills30d, int? ActivePvpKills, double? LowsecRatio, int? DangerRatio, int? GangRatio, DateTimeOffset? LastPvpAt, bool HunterSeen, IReadOnlyList<string> HunterShips, IReadOnlyList<string> Notes)> ResolveZkillAsync(long charId, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "magent-intel/1.0");

        var url = $"https://zkillboard.com/api/stats/characterID/{charId}/";
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return (0, 0, null, null, null, null, null, false, [], ["zKillboard stats unavailable."]);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return (0, 0, null, null, null, null, null, false, [], ["zKillboard stats payload unexpected."]);

        var root = doc.RootElement;
        var now = DateTimeOffset.UtcNow;
        var kills7d = 0;
        var kills30 = 0;
        DateTimeOffset? last = null;

        if (root.TryGetProperty("months", out var months) && months.ValueKind == JsonValueKind.Object)
        {
            foreach (var month in months.EnumerateObject())
            {
                if (month.Value.ValueKind != JsonValueKind.Object) continue;
                if (!month.Value.TryGetProperty("year", out var y) || !y.TryGetInt32(out var year)) continue;
                if (!month.Value.TryGetProperty("month", out var m) || !m.TryGetInt32(out var monthNum)) continue;
                if (year <= 0 || monthNum is < 1 or > 12) continue;

                var destroyed = month.Value.TryGetProperty("shipsDestroyed", out var sd) && sd.TryGetInt32(out var v) ? v : 0;
                var monthStart = new DateTimeOffset(year, monthNum, 1, 0, 0, 0, TimeSpan.Zero);
                var nextMonth = monthStart.AddMonths(1);
                if (nextMonth >= now.AddDays(-30)) kills30 += destroyed;
                if (nextMonth >= now.AddDays(-7)) kills7d += destroyed;
                if (destroyed > 0) last = last is null || monthStart > last ? monthStart : last;
            }
        }

        var lowsecRatio = root.TryGetProperty("labels", out var labels)
            && labels.ValueKind == JsonValueKind.Object
            && labels.TryGetProperty("loc:lowsec", out var lowsecObj)
            && lowsecObj.ValueKind == JsonValueKind.Object
            && lowsecObj.TryGetProperty("shipsDestroyed", out var lowsecKills)
            && lowsecKills.TryGetInt32(out var lowsecDestroyed)
            && root.TryGetProperty("shipsDestroyed", out var totalShips)
            && totalShips.TryGetInt32(out var totalDestroyed)
            && totalDestroyed > 0
            ? (double)lowsecDestroyed / totalDestroyed
            : null;

        var hunterShips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("topLists", out var topLists) && topLists.ValueKind == JsonValueKind.Array)
        {
            foreach (var list in topLists.EnumerateArray())
            {
                if (!list.TryGetProperty("type", out var t) || !string.Equals(t.GetString(), "shipType", StringComparison.OrdinalIgnoreCase)) continue;
                if (!list.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) continue;
                foreach (var ship in values.EnumerateArray())
                {
                    if (!ship.TryGetProperty("shipName", out var nameProp)) continue;
                    var shipName = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(shipName) && HunterShips.Contains(shipName)) hunterShips.Add(shipName);
                }
            }
        }

        int? activePvpKills = null;
        if (root.TryGetProperty("activepvp", out var activePvp) && activePvp.ValueKind == JsonValueKind.Object
            && activePvp.TryGetProperty("kills", out var killsObj) && killsObj.ValueKind == JsonValueKind.Object
            && killsObj.TryGetProperty("count", out var count) && count.TryGetInt32(out var c))
        {
            activePvpKills = c;
        }

        var dangerRatio = root.TryGetProperty("dangerRatio", out var dr) && dr.TryGetInt32(out var danger) ? danger : null;
        var gangRatio = root.TryGetProperty("gangRatio", out var gr) && gr.TryGetInt32(out var gang) ? gang : null;

        return (kills7d, kills30, activePvpKills, lowsecRatio, dangerRatio, gangRatio, last, hunterShips.Count > 0, hunterShips.ToList(), []);
    }
}

internal sealed class EsiSystemIntelSource(IEsiClient esi, ILogger logger) : ISystemIntelSource
{
    public async Task<SystemIntel> ResolveSystemAsync(int? systemId, string? systemName, CancellationToken cancellationToken)
    {
        try
        {
            var kills = await esi.GetSystemKillsAsync(cancellationToken);
            var jumps = await esi.GetSystemJumpsAsync(cancellationToken);
            var kill = systemId is null ? kills.OrderByDescending(x => x.ship_kills).FirstOrDefault() : kills.FirstOrDefault(x => x.system_id == systemId.Value);
            var jump = systemId is null ? jumps.OrderByDescending(x => x.ship_jumps).FirstOrDefault(x => x.system_id == kill?.system_id) : jumps.FirstOrDefault(x => x.system_id == systemId.Value);
            return new SystemIntel(kill?.system_id, systemName, jump?.ship_jumps ?? 0, kill?.ship_kills ?? 0, kill?.pod_kills ?? 0, []);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed system intel lookup.");
            return new SystemIntel(systemId, systemName, 0, 0, 0, ["System intel lookup failed."]);
        }
    }
}
