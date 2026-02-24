using Magent.Core;
using Xunit;

namespace Magent.Core.Tests;

public sealed class OpportunityCalculatorTests
{
    [Fact]
    public void ComputeNetMarginPct_AppliesFees()
    {
        var margin = OpportunityCalculator.ComputeNetMarginPct(100m, 120m, 3m, 4.5m);
        Assert.True(margin > 0m);
        Assert.InRange(margin, 9m, 11m);
    }

    [Fact]
    public void Calculate_ReturnsFlip_WhenSpreadAndVolumePassThresholds()
    {
        var config = new AppConfig(10000043, 60008494, 15, 3m, 4.5m, 2m, 100, 250, 100_000_000m, 20m, 1_000_000m, 10, null);
        var characterOrders = new List<CharacterOrder>();
        var marketOrders = new List<MarketOrder>
        {
            new(1, 34, true, 100m, 500, 60008494, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)),
            new(2, 34, false, 120m, 500, 60008494, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7))
        };
        var volumes = new Dictionary<int, long> { [34] = 500 };

        var sut = new OpportunityCalculator();
        var result = sut.Calculate(config, characterOrders, marketOrders, volumes, 500_000_000m, DateTimeOffset.UtcNow);

        Assert.Contains(result, x => x.Kind == OpportunityKind.Flip && x.TypeId == 34);
    }

    [Fact]
    public void Calculate_ComputesWalletAwareSuggestedQuantity()
    {
        var config = new AppConfig(10000043, 60008494, 15, 3m, 4.5m, 2m, 100, 250, 50_000_000m, 10m, 5_000_000m, 10, null);
        var marketOrders = new List<MarketOrder>
        {
            new(1, 34, true, 100m, 500, 60008494, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7)),
            new(2, 34, false, 120m, 500, 60008494, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7))
        };

        var sut = new OpportunityCalculator();
        var result = sut.Calculate(config, [], marketOrders, new Dictionary<int, long> { [34] = 500 }, 100_000_000m, DateTimeOffset.UtcNow);

        var flip = Assert.Single(result.Where(x => x.Kind == OpportunityKind.Flip));
        Assert.Equal(100000, flip.SuggestedQuantity);
        Assert.Equal(10_000_000m, flip.SuggestedInvestmentIsk);
    }
}
