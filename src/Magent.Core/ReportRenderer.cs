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

        WriteSection(sb, "Orders needing update", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Update));
        WriteSection(sb, "New seed opportunities", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Seed));
        WriteSection(sb, "Flip opportunities", snapshot.Opportunities.Where(x => x.Kind == OpportunityKind.Flip));

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
        var md = ToMarkdown(snapshot)
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\n", "<br/>");

        return $"""
<!doctype html>
<html>
<head>
<meta charset=\"utf-8\" />
<title>Amarr Station Trading Radar</title>
<style>
body {{ font-family: Arial, sans-serif; margin: 2rem; background: #0d1117; color: #e6edf3; }}
.card {{ border: 1px solid #30363d; border-radius: 8px; padding: 1rem; background: #161b22; }}
</style>
</head>
<body>
<div class=\"card\">{md}</div>
</body>
</html>
""";
    }

    private static void WriteSection(StringBuilder sb, string title, IEnumerable<Opportunity> opportunities)
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
            sb.AppendLine($"- Type {item.TypeId}: {item.Title} | Net margin {item.NetMarginPct:N2}% | Profit {item.EstimatedProfitIsk:N2} ISK | Confidence {item.Confidence}");
        }
    }
}
