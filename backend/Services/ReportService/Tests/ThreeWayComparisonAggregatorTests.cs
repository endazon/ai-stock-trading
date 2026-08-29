using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-15, FR-16, FR-20, #569, 04_report-templates 月報 §5, IADR-0251, IADR-0271:
// 三者比較の集計（純関数）。**「空欄（該当なし）」と「値 0」の区別**が中心の関心事である。
public class ThreeWayComparisonAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 14, 0, 0, TimeSpan.Zero);
    private static readonly TradingAssumptions Assumptions = TradingAssumptionsDefaults.Create();

    private static PeriodTradeFill Fill(
        TradeSide side, PositionEffect effect, int quantity, decimal price, BrokerProvider? provider, int minute = 0) =>
        new("AAPL", Market.UnitedStates, side, effect, quantity, price, T0.AddMinutes(minute),
            Guid.NewGuid(), provider);

    // 1 往復（新規建て → 決済）で利益が出る最小の約定列。
    private static IReadOnlyList<PeriodTradeFill> RoundTrip(BrokerProvider? provider) =>
    [
        Fill(TradeSide.Buy, PositionEffect.Open, 10, 100m, provider),
        Fill(TradeSide.Sell, PositionEffect.Close, 10, 120m, provider, minute: 60),
    ];

    // 🔴 **否定形**: 段階を照会できていないなら**節ごと未供給**（null）である。
    // 段階を知らないまま組み立てると、到達していない段の 0 件が「走らせた結果 0 だった」と読まれる。
    [Fact]
    public void 段階が未供給なら節ごと未供給にする()
    {
        ThreeWayComparisonAggregator.Aggregate(RoundTrip(BrokerProvider.MoomooSimulate), Assumptions, null)
            .Should().BeNull();
    }

    // **対の肯定形**: 段階が供給されれば表が組み上がる（不在の表明だけでは、集計が壊れていても緑になる）。
    [Fact]
    public void 段階が供給されれば表が組み上がる()
    {
        var c = ThreeWayComparisonAggregator.Aggregate(
            RoundTrip(BrokerProvider.MoomooSimulate), Assumptions, TradingStage.Stage1Simulate);

        c.Should().NotBeNull();
        c!.TradeCount.Simulate.Should().Be(2);
        c.WinRate.Simulate.Should().Be(1m);
        c.AveragePnlUsd.Simulate.Should().NotBeNull();
    }

    // 🔴 **境界値（計画の明文）**: 「Stage 1 の間は実弾列、Stage 0 の間は SIMULATE 列も空欄」。
    [Theory]
    [InlineData(TradingStage.Stage0Verification, false, false)]
    [InlineData(TradingStage.Stage1Simulate, true, false)]
    [InlineData(TradingStage.Stage2MinimalLive, true, true)]
    [InlineData(TradingStage.Stage3ScaledLive, true, true)]
    public void 到達していない段の列は空欄である(TradingStage stage, bool simulateReached, bool liveReached)
    {
        var c = ThreeWayComparisonAggregator.Aggregate([], Assumptions, stage);

        c.Should().NotBeNull();
        (c!.TradeCount.Simulate is not null).Should().Be(simulateReached);
        (c.TradeCount.Live is not null).Should().Be(liveReached);
    }

    // 🔴 **本 issue の核心**: 到達済みで約定が無い段は **0 件**（空欄ではない）。
    // 空欄は「まだ走らせていない」を意味すると計画が定めており、混ぜると乖離の読み方が反転する。
    [Fact]
    public void 到達済みで約定が無い段は0件であり空欄ではない()
    {
        var c = ThreeWayComparisonAggregator.Aggregate([], Assumptions, TradingStage.Stage2MinimalLive);

        c!.TradeCount.Simulate.Should().Be(0m);
        c.TradeCount.Live.Should().Be(0m);
        // 決済が 1 件も無ければ勝率・平均損益は**定義できない**（0 ではない）。
        c.WinRate.Live.Should().BeNull();
        c.AveragePnlUsd.Live.Should().BeNull();
    }

    // 🔴 発注先で列を分ける。片方の段の約定がもう片方へ混ざらない。
    [Fact]
    public void 発注先ごとに列を分ける()
    {
        var fills = new List<PeriodTradeFill>();
        fills.AddRange(RoundTrip(BrokerProvider.MoomooSimulate));
        fills.Add(Fill(TradeSide.Buy, PositionEffect.Open, 5, 100m, BrokerProvider.MoomooReal, minute: 120));

        var c = ThreeWayComparisonAggregator.Aggregate(fills, Assumptions, TradingStage.Stage2MinimalLive);

        c!.TradeCount.Simulate.Should().Be(2m);
        c.TradeCount.Live.Should().Be(1m);
    }

    // 🔴 **推定で埋めない**: 発注先不明の約定はどの列にも算入せず、**件数を別に出す**。
    // 黙って落とすと「その段では 1 件も取引していない」と読まれる。
    [Fact]
    public void 発注先不明の約定はどの列にも算入せず件数を出す()
    {
        var fills = new List<PeriodTradeFill>
        {
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 100m, provider: null),
            Fill(TradeSide.Buy, PositionEffect.Open, 10, 100m, BrokerProvider.InternalPaper, minute: 30),
        };

        var c = ThreeWayComparisonAggregator.Aggregate(fills, Assumptions, TradingStage.Stage2MinimalLive);

        c!.TradeCount.Simulate.Should().Be(0m);
        c.TradeCount.Live.Should().Be(0m);
        // 内蔵 paper は「発注先が分かっている」ため未算入件数には数えない（分かったうえで対象外）。
        c.UnattributedTradeCount.Should().Be(1);
    }

    // 🔴 バックテスト列は**常に空欄**（供給元が 1 つも無い＝「まだ走らせていない」は事実である）。
    [Fact]
    public void バックテスト列は常に空欄である()
    {
        var c = ThreeWayComparisonAggregator.Aggregate(
            RoundTrip(BrokerProvider.MoomooSimulate), Assumptions, TradingStage.Stage3ScaledLive);

        c!.WinRate.Backtest.Should().BeNull();
        c.AveragePnlUsd.Backtest.Should().BeNull();
        c.MaxDrawdown.Backtest.Should().BeNull();
        c.TradeCount.Backtest.Should().BeNull();
    }

    // 🔴 最大ドローダウンは**どの列も供給しない**（エクイティ曲線の権威源が期間集計を持たない）。
    // 期間の実現損益から別定義の DD を発明しないことを、ここで固定する。
    [Fact]
    public void 最大ドローダウンはどの列も供給しない()
    {
        var c = ThreeWayComparisonAggregator.Aggregate(
            RoundTrip(BrokerProvider.MoomooSimulate), Assumptions, TradingStage.Stage3ScaledLive);

        c!.MaxDrawdown.Should().Be(new ThreeWayMetric(null, null, null));
    }

    // **プロパティ**: 各列の取引件数の合計 ＋ 未算入件数 ＋ 内蔵 paper の件数 ＝ 期間の全約定件数。
    // どの約定も「二重に数える」ことも「黙って消える」こともない。
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(23)]
    public void 列の件数と未算入件数で全約定を説明できる(int count)
    {
        BrokerProvider?[] providers =
            [BrokerProvider.MoomooSimulate, BrokerProvider.MoomooReal, BrokerProvider.InternalPaper, null];
        var fills = Enumerable.Range(0, count)
            .Select(i => Fill(TradeSide.Buy, PositionEffect.Open, 1, 100m, providers[i % providers.Length], i))
            .ToList();

        var c = ThreeWayComparisonAggregator.Aggregate(fills, Assumptions, TradingStage.Stage2MinimalLive);

        var internalPaper = fills.Count(f => f.Provider == BrokerProvider.InternalPaper);
        ((int)c!.TradeCount.Simulate! + (int)c.TradeCount.Live! + c.UnattributedTradeCount + internalPaper)
            .Should().Be(count);
    }

    // 🔴 集計は**決定的**である（同じ入力なら同じ結果。散文・時刻・カルチャに依存しない）。
    [Fact]
    public void 同じ入力なら同じ結果になる()
    {
        var fills = RoundTrip(BrokerProvider.MoomooSimulate);

        ThreeWayComparisonAggregator.Aggregate(fills, Assumptions, TradingStage.Stage2MinimalLive)
            .Should().Be(ThreeWayComparisonAggregator.Aggregate(fills, Assumptions, TradingStage.Stage2MinimalLive));
    }
}
