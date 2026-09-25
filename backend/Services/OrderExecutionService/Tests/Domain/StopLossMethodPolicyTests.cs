using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342 決定4: 手法の解決（純関数）の全組み合わせ表。
// 4 手法（＋未知）× 3 発注先 × 2 向き（買い・空売り）を網羅し、判定の順序（S0 → 発注先 → 空売り → S2 → S1 → その他）を固定する。
// #820, IADR-0344 決定2: S1 は SIMULATE の新規買いで SoftwareStop に解決される。
// #821, IADR-0347: S3 は SIMULATE の新規買いで AlternativeBrokerOrderType に解決される（未知だけが S0 へのフォールバック）。
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

        // #820, IADR-0344 決定2 / #821, IADR-0347: S1 も S3 も実装済み（未実装の fallback に残るのは未知だけ）。
        return method switch
        {
            StopLossExecutionMethod.NoProtectiveStop => StopLossMethodDisposition.ProtectiveStopWaived,
            StopLossExecutionMethod.SoftwareStop => StopLossMethodDisposition.SoftwareStop,
            StopLossExecutionMethod.AlternativeBrokerOrderType => StopLossMethodDisposition.AlternativeBrokerOrderType,
            _ => StopLossMethodDisposition.NotImplementedFallbackToBrokerStop,
        };
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

    [Fact]
    public void SIMULATEのS1の新規買いはソフトウェア逆指値になる()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.SoftwareStop, Entry(ProductType.Cash, TradeSide.Buy), BrokerProvider.MoomooSimulate)
            .Should().Be(StopLossMethodDisposition.SoftwareStop);
    }

    [Fact]
    public void 空売りはSIMULATEのS1でもソフトウェア逆指値にならない()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.SoftwareStop, Entry(ProductType.ShortSell, TradeSide.Sell),
                BrokerProvider.MoomooSimulate)
            .Should().Be(StopLossMethodDisposition.BrokerStopOrder);
    }

    [Fact]
    public void 実弾ではS1は拒否される()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.SoftwareStop, Entry(ProductType.Cash, TradeSide.Buy), BrokerProvider.MoomooReal)
            .Should().Be(StopLossMethodDisposition.Refused);
    }
    // #821, IADR-0347: S3 は SIMULATE の買いにだけ届く（実弾・空売りは従来の統制のまま）。
    [Fact]
    public void 実弾ではS3は代替注文種別にならない()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.AlternativeBrokerOrderType, Entry(ProductType.Cash, TradeSide.Buy),
                BrokerProvider.MoomooReal)
            .Should().Be(StopLossMethodDisposition.Refused);
    }

    [Fact]
    public void 空売りはSIMULATEのS3でも代替注文種別にならない()
    {
        StopLossMethodPolicy.Resolve(
                StopLossExecutionMethod.AlternativeBrokerOrderType, Entry(ProductType.ShortSell, TradeSide.Sell),
                BrokerProvider.MoomooSimulate)
            .Should().Be(StopLossMethodDisposition.BrokerStopOrder);
    }

    // ---- T-10-1080, FR-10, FR-06, #1002, IADR-0429 決定2: 解決と理由・適用した手法 ----------------------------------

    // 期待する理由は判定の順序（S0 → 発注先 → 空売り → 既知の手法 → 未知）を書き下したもの。
    private static StopLossMethodResolutionReason ExpectedReason(
        StopLossExecutionMethod method, BrokerProvider provider, ProductType product)
    {
        if (method == StopLossExecutionMethod.BrokerStopOrder)
            return StopLossMethodResolutionReason.AsSelected;
        if (provider != BrokerProvider.MoomooSimulate)
            return StopLossMethodResolutionReason.BrokerNotMoomooSimulate;
        if (product == ProductType.ShortSell)
            return StopLossMethodResolutionReason.ShortSellEntry;
        return Enum.IsDefined(method) ? StopLossMethodResolutionReason.AsSelected : StopLossMethodResolutionReason.UnknownMethod;
    }

    // 🔴 解決は 1 か所で決まる: 全組み合わせで ResolveWithReason の解決結果は Resolve と同じであり、理由は順序どおり。
    // 🔴 「適用した手法が承認の手法と同じ」⇔「理由が AsSelected」も全組み合わせで成り立つ（日報の食い違いの定義の土台）。
    [Theory]
    [MemberData(nameof(Table))]
    public void T_10_1080_解決と理由は1か所で決まり全組み合わせで一致の定義と噛み合う(
        StopLossExecutionMethod method, BrokerProvider provider, ProductType product, StopLossMethodDisposition expected)
    {
        var side = product == ProductType.ShortSell ? TradeSide.Sell : TradeSide.Buy;

        var resolution = StopLossMethodPolicy.ResolveWithReason(method, Entry(product, side), provider);

        resolution.Disposition.Should().Be(expected);
        resolution.Reason.Should().Be(ExpectedReason(method, provider, product));
        var applied = StopLossMethodPolicy.AppliedMethodOf(resolution.Disposition);
        (applied == method).Should().Be(resolution.Reason == StopLossMethodResolutionReason.AsSelected,
            "食い違い（適用 ≠ 選択）は理由が AsSelected でないときに限る");
    }

    [Theory]
    [InlineData(StopLossMethodDisposition.BrokerStopOrder, StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossMethodDisposition.NotImplementedFallbackToBrokerStop, StopLossExecutionMethod.BrokerStopOrder)]
    [InlineData(StopLossMethodDisposition.ProtectiveStopWaived, StopLossExecutionMethod.NoProtectiveStop)]
    [InlineData(StopLossMethodDisposition.SoftwareStop, StopLossExecutionMethod.SoftwareStop)]
    [InlineData(StopLossMethodDisposition.AlternativeBrokerOrderType, StopLossExecutionMethod.AlternativeBrokerOrderType)]
    public void T_10_1080_解決結果から適用した手法を写す(StopLossMethodDisposition disposition, StopLossExecutionMethod applied)
    {
        StopLossMethodPolicy.AppliedMethodOf(disposition).Should().Be(applied);
    }

    // 🔴 否定形: 拒否は S0 へ読み替えない（適用なし）。
    [Fact]
    public void T_10_1080_拒否は適用なしでありS0と書かない()
    {
        StopLossMethodPolicy.AppliedMethodOf(StopLossMethodDisposition.Refused).Should().BeNull();
        Enum.GetValues<StopLossMethodDisposition>()
            .Should().OnlyContain(d => d == StopLossMethodDisposition.Refused || StopLossMethodPolicy.AppliedMethodOf(d) != null,
                "解決結果を足したら写しも足す（写し漏れは例外になる）");
    }
}
