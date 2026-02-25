using Magent.Core;
using Xunit;

namespace Magent.Core.Tests;

public sealed class IntelTests
{
    [Fact]
    public void LocalChatParser_ExtractsSpeakerName()
    {
        var parser = new LocalChatParser();
        var names = parser.ExtractPilotNames("[ 2026.01.01 12:00:00 ] Zara Kestel > o/");
        var name = Assert.Single(names);
        Assert.Equal("Zara Kestel", name);
    }

    [Fact]
    public void PilotThreatScorer_ProducesExpectedBandAndReasons()
    {
        var scorer = new PilotThreatScorer();
        var intel = new PilotIntel("Hunter", 1, 2, "Bad Corp", 3, "Bad Alliance", -6, 40, 50, 35, 0.8, 94, 90, DateTimeOffset.UtcNow.AddMinutes(-30), true, ["Astero"], []);
        var result = scorer.Score(intel, [new DenylistEntry("corp", "Bad", 15, null)], DateTimeOffset.UtcNow);
        Assert.Equal(ThreatBand.Extreme, result.Band);
        Assert.InRange(result.Score, 80, 100);
        Assert.Contains(result.Reasons, r => r.Contains("Denylist"));
    }

    [Fact]
    public void AlertCooldownGate_DedupesUntilCooldownOrBandIncrease()
    {
        var gate = new AlertCooldownGate();
        var now = DateTimeOffset.UtcNow;
        Assert.True(gate.ShouldAlert("pilot:alice", ThreatBand.Med, now, TimeSpan.FromMinutes(5)));
        Assert.False(gate.ShouldAlert("pilot:alice", ThreatBand.Med, now.AddMinutes(1), TimeSpan.FromMinutes(5)));
        Assert.True(gate.ShouldAlert("pilot:alice", ThreatBand.High, now.AddMinutes(2), TimeSpan.FromMinutes(5)));
        Assert.True(gate.ShouldAlert("pilot:alice", ThreatBand.High, now.AddMinutes(6), TimeSpan.FromMinutes(5)));
    }
}
