namespace Magent.Core;

public sealed class OpportunityCalculator
{
    public IReadOnlyList<Opportunity> Calculate(
        AppConfig config,
        IReadOnlyList<CharacterOrder> characterOrders,
        IReadOnlyList<MarketOrder> marketOrders,
        IReadOnlyDictionary<int, long> dailyVolumes,
        DateTimeOffset nowUtc)
    {
        var opportunities = new List<Opportunity>();
        var typeIds = marketOrders.Select(x => x.TypeId).Union(characterOrders.Select(x => x.TypeId)).Distinct();

        foreach (var typeId in typeIds)
        {
            var typeMarket = marketOrders.Where(x => x.TypeId == typeId && x.LocationId == config.HubLocationId).ToList();
            var typeOwn = characterOrders.Where(x => x.TypeId == typeId && x.LocationId == config.HubLocationId).ToList();

            if (typeMarket.Count == 0)
            {
                continue;
            }

            var bestBuy = typeMarket.Where(x => x.IsBuyOrder).OrderByDescending(x => x.Price).FirstOrDefault();
            var bestSell = typeMarket.Where(x => !x.IsBuyOrder).OrderBy(x => x.Price).FirstOrDefault();
            var volume = dailyVolumes.TryGetValue(typeId, out var v) ? v : 0;

            if (bestBuy is not null && bestSell is not null)
            {
                var netMarginPct = ComputeNetMarginPct(bestBuy.Price, bestSell.Price, config.BrokerFeePct, config.SalesTaxPct);
                var estProfit = Math.Max(0, bestSell.Price - bestBuy.Price) * Math.Max(1, Math.Min(bestBuy.VolumeRemain, bestSell.VolumeRemain));

                if (netMarginPct >= config.MinNetMarginPct && volume >= config.MinDailyVolume)
                {
                    opportunities.Add(CreateOpportunity(OpportunityKind.Flip, typeId, netMarginPct, estProfit, volume, nowUtc, $"Flip spread exists for type {typeId}"));
                }
            }

            var ownSell = typeOwn.Where(x => !x.IsBuyOrder).OrderBy(x => x.Price).FirstOrDefault();
            var marketBestSell = typeMarket.Where(x => !x.IsBuyOrder).OrderBy(x => x.Price).FirstOrDefault();
            if (ownSell is not null && marketBestSell is not null)
            {
                var undercut = ownSell.Price > marketBestSell.Price;
                var expiryRisk = ownSell.ExpiresAt <= nowUtc.AddHours(24);
                if (undercut || expiryRisk)
                {
                    opportunities.Add(new Opportunity(
                        OpportunityKind.Update,
                        typeId,
                        $"Update order for type {typeId}",
                        undercut ? "Order is undercut at hub." : "Order expires within 24 hours.",
                        0,
                        0,
                        volume,
                        ConfidenceLevel.Medium,
                        nowUtc,
                        $"update:{typeId}:{(undercut ? "u" : "e")}"));
                }
            }

            if (typeOwn.Count == 0 && bestBuy is not null && bestSell is not null)
            {
                var netMarginPct = ComputeNetMarginPct(bestBuy.Price, bestSell.Price, config.BrokerFeePct, config.SalesTaxPct);
                var estProfit = Math.Max(0, bestSell.Price - bestBuy.Price) * Math.Max(1, Math.Min(bestBuy.VolumeRemain, bestSell.VolumeRemain));
                if (netMarginPct >= config.MinNetMarginPct && volume >= config.MinDailyVolume)
                {
                    opportunities.Add(CreateOpportunity(OpportunityKind.Seed, typeId, netMarginPct, estProfit, volume, nowUtc, "No active order but spread looks healthy."));
                }
            }
        }

        return opportunities;
    }

    public static decimal ComputeNetMarginPct(decimal bestBuy, decimal bestSell, decimal brokerFeePct, decimal salesTaxPct)
    {
        if (bestBuy <= 0 || bestSell <= 0)
        {
            return 0;
        }

        var buyCost = bestBuy * (1 + brokerFeePct / 100m);
        var sellRevenue = bestSell * (1 - (brokerFeePct + salesTaxPct) / 100m);
        return (sellRevenue - buyCost) / buyCost * 100m;
    }

    private static Opportunity CreateOpportunity(OpportunityKind kind, int typeId, decimal netMarginPct, decimal estProfit, long volume, DateTimeOffset nowUtc, string notes)
    {
        var confidence = volume >= 1000 && netMarginPct >= 8 ? ConfidenceLevel.High : volume >= 300 ? ConfidenceLevel.Medium : ConfidenceLevel.Low;
        return new Opportunity(
            kind,
            typeId,
            $"{kind} opportunity for type {typeId}",
            notes,
            Math.Round(netMarginPct, 2),
            Math.Round(estProfit, 2),
            volume,
            confidence,
            nowUtc,
            $"{kind.ToString().ToLowerInvariant()}:{typeId}:{Math.Round(netMarginPct, 2)}");
    }
}
