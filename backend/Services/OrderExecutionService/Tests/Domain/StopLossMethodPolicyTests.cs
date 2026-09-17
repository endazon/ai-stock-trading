using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342 決定4: 手法の解決（純関数）の全組み合わせ表。
// 4 手法（＋未知）× 3 発注先 × 2 向き（買い・空売り）を網羅し、判定の順序（S0 → 発注先 → 空売り → S2 → その他）を固定する。
public class StopLossMethodPolicyTests
{
    private static OrderIntent Entry(ProductType productType, TradeSide side) =>
        new("AAPL", Market.UnitedStates, side, productType, BrokerProvider.MoomooSimulate, 10, 1_000m,
            PositionEffect.Open, StopLossPrice: 950m);

    public static TheoryData<StopLossExecutionMethod, BrokerProvider, ProductType, StopLossMethodDisposition> Table()
    {
        var data = new TheoryData<StopLossExecutionMethod, BrokerProvider, ProductType, StopLossMethodDisposition>();
        var methods = new[]
        {
            StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.SoftwareStop,
            StopLossExecutionMethod.NoProtectiveStop, StopLossExecutionMethod.AlternativeBrokerOrderType,
            (StopLossExecutionMethod)42,
        };
        var providers = new[] { BrokerProvider.InternalPaper, BrokerProvider.MoomooReal, BrokerProvider.MoomooSimulate };
        var products = new[] { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell };

        var cases =
            from method in methods
            from provider in providers
            from product in products
            select (method, provider, product);
        foreach (var (method, provider, product) in cases)
        {
            data.Add(method, provider, product, Expected(method, provider, product));
        }

        return data;
    }

    // 期待値は判定の順序（S0 → 発注先 → 空売り → S2 → その他）をそのまま書き下したもの。
    private static StopLossMethodDisposition Expected(
        StopLossExecutionMethod method, BrokerProvider provider, ProductType product)
    {
        if (method == StopLossExecutionMethod.BrokerStopOrder)
        {
            return StopLossMethodDisposition.BrokerStopOrder;
        }

        if (provider != BrokerProvider.MoomooSimulate)
        {
            return StopLossMethodDisposition.Refused;
        }

        if (product == ProductType.ShortSell)
        {
            return StopLossMethodDisposition.BrokerStopOrder;
        }

        return method == StopLossExecutionMethod.NoProtectiveStop
            ? StopLossMethodDisposition.ProtectiveStopWaived
            : StopLossMethodDisposition.NotImplementedFallbackToBrokerStop;
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void 手法と発注先と商品種別から扱いが一意に決まる(
        StopLossExecutionMethod method, BrokerProvider provider, ProductType product, StopLossMethodDisposition expected)
    {
        var side = product == ProductType.ShortSell ? TradeSide.Sell : TradeSide.Buy;

        StopLossMethodPolicy.Resolve(method, Entry(product, side), provider).Should().Be(expected);
    }

    // 🔴 否定形の要点を名前で固定する（表だけだと何を守っているかが読めない）。
    [Fact]
    public void 実弾ではS2は免除にならない()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.NoProtectiveStop, Entry(ProductType.Cash, TradeSide.Buy), BrokerProvider.MoomooReal)
            .Should().Be(StopLossMethodDisposition.Refused);
    }

    [Fact]
    public void 空売りはSIMULATEのS2でも免除にならない()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.NoProtectiveStop, Entry(ProductType.ShortSell, TradeSide.Sell),
                BrokerProvider.MoomooSimulate)
            .Should().Be(StopLossMethodDisposition.BrokerStopOrder);
    }
}
