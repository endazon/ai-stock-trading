using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Domain.Tests;

// FR-05, FR-16: 実効スリッページ算出（不利＝正）の検証。
public class SlippageCalculatorTests
{
    [Fact]
    public void 買いが高く約定すると不利で正のスリッページ()
    {
        // 計画 1000 → 約定 1010（+1%）。買いは高く約定＝不利。
        SlippageCalculator.Compute(1_000m, 1_010m, TradeSide.Buy).Should().Be(0.01m);
    }

    [Fact]
    public void 買いが安く約定すると有利で負のスリッページ()
    {
        SlippageCalculator.Compute(1_000m, 990m, TradeSide.Buy).Should().Be(-0.01m);
    }

    [Fact]
    public void 売りが安く約定すると不利で正のスリッページ()
    {
        // 売りは安く約定＝不利。計画 1000 → 約定 990。
        SlippageCalculator.Compute(1_000m, 990m, TradeSide.Sell).Should().Be(0.01m);
    }

    [Fact]
    public void 売りが高く約定すると有利で負のスリッページ()
    {
        SlippageCalculator.Compute(1_000m, 1_010m, TradeSide.Sell).Should().Be(-0.01m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 計画価格がゼロ以下ならスリッページはゼロ(int planned)
    {
        SlippageCalculator.Compute(planned, 1_000m, TradeSide.Buy).Should().Be(0m);
    }

    [Fact]
    public void 約定価格がゼロ以下なら未約定としてスリッページはゼロ()
    {
        // ブローカ拒否（AveragePrice 0）は未約定扱い。
        SlippageCalculator.Compute(1_000m, 0m, TradeSide.Buy).Should().Be(0m);
    }
}
