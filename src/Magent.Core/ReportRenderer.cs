using System.Text;

namespace Magent.Core;

public static class ReportRenderer
{
    public static string ToMarkdown(RadarSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Amarr Station Trading Radar");
        sb.AppendLine();
        sb.AppendLine($"Timestamp: {snapshot.Timestamp:O}");
        sb.AppendLine();
        sb.AppendLine("## Wallet summary");
        sb.AppendLine($"- Balance: {snapshot.Wallet.Balance:N2} ISK");
        sb.AppendLine($"- Escrow: {snapshot.Wallet.Escrow:N2} ISK");
        sb.AppendLine($"- Buy order value: {snapshot.Wallet.BuyOrderValue:N2} ISK");
        sb.AppendLine($"- Sell order value: {snapshot.Wallet.SellOrderValue:N2} ISK");

        WriteSection(sb, "Orders needing update", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Update), snapshot.TypeNames);
        WriteSection(sb, "New seed opportunities", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Seed), snapshot.TypeNames);
        WriteSection(sb, "Flip opportunities", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Flip), snapshot.TypeNames);

        sb.AppendLine();
        sb.AppendLine("## Historical performance");
        sb.AppendLine($"- Total tracked recommendations: {snapshot.Performance.TotalRecommendations}");
        sb.AppendLine($"- Active recommendations: {snapshot.Performance.ActiveRecommendations}");
        sb.AppendLine($"- Improved recommendations: {snapshot.Performance.ImprovedRecommendations}");
        sb.AppendLine($"- Expired recommendations: {snapshot.Performance.ExpiredRecommendations}");
        sb.AppendLine($"- Improvement rate: {snapshot.Performance.ImprovementRatePct:N2}%");

        sb.AppendLine("## Risk notes");
        if (snapshot.RiskNotes.Count == 0)
        {
            sb.AppendLine("- None");
        }
        else
        {
            foreach (var note in snapshot.RiskNotes)
            {
                sb.AppendLine($"- {note}");
            }
        }

        return sb.ToString();
    }

    public static string ToHtml(RadarSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.Append(
"""
<!doctype html>
<html>
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Amarr Station Trading Radar</title>
<style>
:root {
    --bg: #0d1117;
    --panel: #161b22;
    --panel-soft: #1f2630;
    --border: #30363d;
    --text: #e6edf3;
    --muted: #8b949e;
    --accent: #58a6ff;
    --good: #3fb950;
    --warn: #d29922;
    --risk: #f85149;
}
* { box-sizing: border-box; }
body {
    font-family: Inter, Segoe UI, Arial, sans-serif;
    margin: 0;
    background: var(--bg);
    color: var(--text);
}
.layout {
    max-width: 1200px;
    margin: 0 auto;
    padding: 1.5rem;
}
h1, h2 { margin: 0; }
.muted { color: var(--muted); }
.section {
    margin-top: 1rem;
    border: 1px solid var(--border);
    border-radius: 10px;
    background: var(--panel);
    overflow: hidden;
}
.section-header {
    padding: 0.8rem 1rem;
    border-bottom: 1px solid var(--border);
    background: var(--panel-soft);
}
.section-body { padding: 1rem; }
.kpi-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(170px, 1fr));
    gap: 0.75rem;
}
.kpi {
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 0.75rem;
    background: #11161d;
}
.kpi-label { font-size: 0.8rem; color: var(--muted); margin-bottom: 0.2rem; }
.kpi-value { font-size: 1.1rem; font-weight: 700; }
table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}
th, td {
    border-bottom: 1px solid var(--border);
    padding: 0.55rem;
    text-align: left;
    vertical-align: top;
}
th {
    color: var(--muted);
    font-weight: 600;
    background: #121820;
}
.numeric { text-align: right; white-space: nowrap; }
.confidence {
    display: inline-block;
    min-width: 72px;
    text-align: center;
    font-size: 0.78rem;
    font-weight: 700;
    padding: 0.2rem 0.45rem;
    border-radius: 999px;
}
.confidence-high { color: #d2fbdc; background: rgba(63, 185, 80, 0.2); border: 1px solid rgba(63, 185, 80, 0.7); }
.confidence-medium { color: #ffedc2; background: rgba(210, 153, 34, 0.2); border: 1px solid rgba(210, 153, 34, 0.7); }
.confidence-low { color: #ffd0cd; background: rgba(248, 81, 73, 0.2); border: 1px solid rgba(248, 81, 73, 0.7); }
ul { margin: 0; padding-left: 1.25rem; }
.none { color: var(--muted); font-style: italic; }
</style>
</head>
<body>
<main class="layout">
  <header>
    <h1>Amarr Station Trading Radar</h1>
    <p class="muted">Generated at 
"""
    );
        sb.Append(Escape(snapshot.Timestamp.ToString("u")));
        sb.AppendLine("""
        </p>
  </header>
""");

        sb.AppendLine("""
  <section class="section">
    <div class="section-header"><h2>Wallet Summary</h2></div>
    <div class="section-body">
      <div class="kpi-grid">
""");
        AppendKpi(sb, "Balance", $"{snapshot.Wallet.Balance:N2} ISK");
        AppendKpi(sb, "Escrow", $"{snapshot.Wallet.Escrow:N2} ISK");
        AppendKpi(sb, "Buy Order Value", $"{snapshot.Wallet.BuyOrderValue:N2} ISK");
        AppendKpi(sb, "Sell Order Value", $"{snapshot.Wallet.SellOrderValue:N2} ISK");
        sb.AppendLine("""
      </div>
    </div>
  </section>
""");

        AppendOpportunitySection(sb, "Orders Needing Update", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Update), snapshot.TypeNames);
        AppendOpportunitySection(sb, "New Seed Opportunities", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Seed), snapshot.TypeNames);
        AppendOpportunitySection(sb, "Flip Opportunities", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Flip), snapshot.TypeNames);

        sb.AppendLine("""
  <section class="section">
    <div class="section-header"><h2>Historical Performance</h2></div>
    <div class="section-body">
      <div class="kpi-grid">
""");
        AppendKpi(sb, "Total Recommendations", snapshot.Performance.TotalRecommendations.ToString("N0"));
        AppendKpi(sb, "Active Recommendations", snapshot.Performance.ActiveRecommendations.ToString("N0"));
        AppendKpi(sb, "Improved Recommendations", snapshot.Performance.ImprovedRecommendations.ToString("N0"));
        AppendKpi(sb, "Expired Recommendations", snapshot.Performance.ExpiredRecommendations.ToString("N0"));
        AppendKpi(sb, "Improvement Rate", $"{snapshot.Performance.ImprovementRatePct:N2}%");

        sb.AppendLine("""
      </div>
    </div>
  </section>

  <section class="section">
    <div class="section-header"><h2>Risk Notes</h2></div>
    <div class="section-body">
""");

        if (snapshot.RiskNotes.Count == 0)
        {
            sb.AppendLine("<p class=\"none\">No active risk notes.</p>");
        }
        else
        {
            sb.AppendLine("<ul>");
            foreach (var note in snapshot.RiskNotes)
            {
                sb.Append("<li>");
                sb.Append(Escape(note));
                sb.AppendLine("</li>");
            }

            sb.AppendLine("</ul>");
        }

        sb.AppendLine("""
    </div>
  </section>
</main>
</body>
</html>
""");

        return sb.ToString();
    }

    private static void AppendKpi(StringBuilder sb, string label, string value)
    {
        sb.Append("<article class=\"kpi\"><div class=\"kpi-label\">");
        sb.Append(Escape(label));
        sb.Append("</div><div class=\"kpi-value\">");
        sb.Append(Escape(value));
        sb.AppendLine("</div></article>");
    }

    private static void AppendOpportunitySection(StringBuilder sb, string title, IEnumerable<Opportunity> opportunities, IReadOnlyDictionary<int, string> typeNames)
    {
        var items = opportunities.ToList();
        sb.Append("""
  <section class="section">
    <div class="section-header"><h2>
  """);
        sb.Append(Escape(title));
        sb.AppendLine("</h2></div>");
        sb.AppendLine("    <div class=\"section-body\">");

        if (items.Count == 0)
        {
            sb.AppendLine("      <p class=\"none\">No opportunities in this category.</p>");
        }
        else
        {
            sb.AppendLine("""
      <table>
        <thead>
          <tr>
            <th>Item</th>
            <th>Title</th>
            <th class="numeric">Buy</th>
            <th class="numeric">Sell</th>
            <th class="numeric">Net Margin</th>
            <th class="numeric">Profit</th>
            <th class="numeric">Daily Volume</th>
            <th class="numeric">Suggested Qty</th>
            <th class="numeric">Suggested ISK</th>
            <th>Confidence</th>
          </tr>
        </thead>
        <tbody>
""");

            foreach (var item in items)
            {
                var typeLabel = typeNames.TryGetValue(item.TypeId, out var name) ? $"{name} ({item.TypeId})" : $"Type {item.TypeId}";
                sb.AppendLine("<tr>");
                sb.Append("<td>"); sb.Append(Escape(typeLabel)); sb.AppendLine("</td>");
                sb.Append("<td>"); sb.Append(Escape(item.Title)); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.BestBuyPrice:N2}")); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.BestSellPrice:N2}")); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.NetMarginPct:N2}%")); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.EstimatedProfitIsk:N2} ISK")); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.DailyVolume:N0}")); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.SuggestedQuantity:N0}")); sb.AppendLine("</td>");
                sb.Append("<td class=\"numeric\">"); sb.Append(Escape($"{item.SuggestedInvestmentIsk:N2} ISK")); sb.AppendLine("</td>");
                sb.Append("<td>"); sb.Append(ConfidenceBadge(item.Confidence)); sb.AppendLine("</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("""
        </tbody>
      </table>
""");
        }

        sb.AppendLine("    </div>");
        sb.AppendLine("  </section>");
    }

    private static string ConfidenceBadge(ConfidenceLevel confidence)
    {
        var cssClass = confidence switch
        {
            ConfidenceLevel.High => "confidence confidence-high",
            ConfidenceLevel.Medium => "confidence confidence-medium",
            _ => "confidence confidence-low"
        };

        return $"<span class=\"{cssClass}\">{Escape(confidence.ToString())}</span>";
    }

    private static string Escape(string input)
        => input
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    private static void WriteSection(StringBuilder sb, string title, IEnumerable<Opportunity> opportunities, IReadOnlyDictionary<int, string> typeNames)
    {
        sb.AppendLine();
        sb.AppendLine($"## {title}");
        var items = opportunities.ToList();
        if (items.Count == 0)
        {
            sb.AppendLine("- None");
            return;
        }

        foreach (var item in items)
        {
            var typeLabel = typeNames.TryGetValue(item.TypeId, out var name) ? $"{name} ({item.TypeId})" : $"Type {item.TypeId}";
            sb.AppendLine($"- {typeLabel}: {item.Title} | Buy {item.BestBuyPrice:N2} | Sell {item.BestSellPrice:N2} | Net margin {item.NetMarginPct:N2}% | Profit {item.EstimatedProfitIsk:N2} ISK | Daily volume {item.DailyVolume:N0} | Suggested qty {item.SuggestedQuantity:N0} | Suggested ISK {item.SuggestedInvestmentIsk:N2} | Confidence {item.Confidence}");
        }
    }
}
