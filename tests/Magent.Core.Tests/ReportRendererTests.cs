using Magent.Core;
using Xunit;

namespace Magent.Core.Tests;

public sealed class ReportRendererTests
{
    [Fact]
    public void ToHtml_RendersStyledTablesAndSections()
    {
        var snapshot = CreateSnapshot();

        var html = ReportRenderer.ToHtml(snapshot);

        Assert.Contains("<table>", html);
        Assert.Contains("Wallet Summary", html);
        Assert.Contains("Orders Needing Update", html);
        Assert.Contains("confidence confidence-high", html);
        Assert.Contains("No opportunities in this category.", html);
    }

    [Fact]
    public void ToHtml_EscapesContent()
    {
        var snapshot = CreateSnapshot(typeName: "<Badger & Co>", title: "Seed <Fast>", riskNote: "Use <caution> & verify");

        var html = ReportRenderer.ToHtml(snapshot);

        Assert.Contains("&lt;Badger &amp; Co&gt;", html);
        Assert.Contains("Seed &lt;Fast&gt;", html);
        Assert.Contains("Use &lt;caution&gt; &amp; verify", html);
        Assert.DoesNotContain("<Badger & Co>", html);
    }

    private static RadarSnapshot CreateSnapshot(
        string typeName = "Tritanium",
        string title = "Relist undercut",
        string riskNote = "Thin volume in late UTC")
    {
        return new RadarSnapshot(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            new WalletSummary(1_000_000m, 50_000m, 120_000m, 240_000m),
            [],
            [],
            [
                new Opportunity(
                    OpportunityKind.Update,
                    34,
                    title,
                    "notes",
                    12.5m,
                    100_000m,
                    10.25m,
                    12.1m,
                    14_000,
                    120,
                    1_230m,
                    ConfidenceLevel.High,
                    DateTimeOffset.UtcNow,
                    "fp-1")
            ],
            new Dictionary<int, string> { [34] = typeName },
            new PerformanceSnapshot(10, 5, 3, 2, 30m),
            [riskNote]);
    }
}
