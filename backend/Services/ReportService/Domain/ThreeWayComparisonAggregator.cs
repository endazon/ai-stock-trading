using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, FR-15, FR-16, FR-20, #569, 04_report-templates 月報 §5, IADR-0251, IADR-0271:
// 三者比較（バックテスト / SIMULATE / 実弾）の集計（**純関数・決定的・副作用なし**）。
//
// 🔴 **LLM に計算させない。** 本関数が唯一の算出経路であり、散文（ReportNarrativeContext）へは渡さない。
//
// 🔴 **「空欄（該当なし）」と「値が 0」を区別する**（計画の明文）。区別の鍵は**現在の運用段階**である。
// 段に到達していなければ列は空欄、到達済みで約定が無ければ取引件数は 0 である。
// **段を知らずに「約定 0 件」を 0 と書くと、まだ到達していない段を「走らせた結果 0 だった」と読ませる。**
public static class ThreeWayComparisonAggregator
{
    /// <summary>SIMULATE 列の発注先（moomoo <c>SIMULATE</c>。FR-20・IADR-0142 の許可制と同じ値）。</summary>
    public const BrokerProvider SimulateProvider = BrokerProvider.MoomooSimulate;

    /// <summary>実弾列の発注先（moomoo <c>REAL</c>）。</summary>
    public const BrokerProvider LiveProvider = BrokerProvider.MoomooReal;

    /// <summary>
    /// 当期間の三者比較。<paramref name="currentStage"/> が <c>null</c>（段階を照会できていない）なら
    /// <b>節ごと未供給</b>（<c>null</c>）を返す——「照会できませんでした」と描かせる。
    /// <para>
    /// バックテスト列は<b>常に空欄</b>である。<c>BacktestService</c> は永続化もイベント発行も持たず、
    /// 契約（<c>BacktestEvaluated</c>）は勝率・平均損益・取引件数を運ばない。
    /// **「まだ走らせていない」は事実であり、空欄はその事実の正しい表現である。**
    /// </para>
    /// <para>
    /// 最大ドローダウンは<b>どの列も供給しない</b>。DD は<b>エクイティ曲線</b>に対する比率であり、
    /// その権威源は段階ゲートの実績（段の窓で latch する観測値）であって期間集計ではない。
    /// **報告書側で期間の実現損益から別定義の DD を発明すると、同じ語が 2 つの意味を持つ**——
    /// 分母を発明しないという既存の規律（<see cref="OpenDUptimeRecord"/> の明文）と同じ理由で供給しない。
    /// </para>
    /// </summary>
    public static ThreeWayComparison? Aggregate(
        IReadOnlyList<PeriodTradeFill> fills,
        TradingAssumptions assumptions,
        TradingStage? currentStage)
    {
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentNullException.ThrowIfNull(assumptions);

        if (currentStage is not { } stage)
            return null;

        var simulate = Column(fills, assumptions, SimulateProvider, reached: stage >= TradingStage.Stage1Simulate);
        var live = Column(fills, assumptions, LiveProvider, reached: stage >= TradingStage.Stage2MinimalLive);

        return new ThreeWayComparison(
            new ThreeWayMetric(null, simulate.WinRate, live.WinRate),
            new ThreeWayMetric(null, simulate.AveragePnl, live.AveragePnl),
            // 最大ドローダウンは 3 列とも未供給（上記の理由）。レンダラが別行で明記する。
            new ThreeWayMetric(null, null, null),
            new ThreeWayMetric(null, simulate.TradeCount, live.TradeCount),
            DivergenceNote: null,
            UnattributedTradeCount: fills.Count(f => f.Provider is null));
    }

    // 1 列ぶん。**到達していない段はすべて null（空欄）**、到達済みなら約定 0 件でも取引件数は 0 を返す。
    private static ColumnMetrics Column(
        IReadOnlyList<PeriodTradeFill> fills,
        TradingAssumptions assumptions,
        BrokerProvider provider,
        bool reached)
    {
        if (!reached)
            return new ColumnMetrics(null, null, null);

        var partition = fills.Where(f => f.Provider == provider).ToList();
        var pnl = PnlAggregator.Aggregate(partition, assumptions);

        // 勝率・平均損益は**決済が 1 件も無ければ定義できない**（0 ではない）。
        // 分母 0 を 0 と書くと「勝率 0%＝全敗」「平均損益 0＝損得なし」と読める。
        var winRate = pnl.RealizingTradeCount == 0
            ? (decimal?)null
            : (decimal)pnl.WinningTradeCount / pnl.RealizingTradeCount;
        var averagePnl = pnl.RealizingTradeCount == 0
            ? (decimal?)null
            : pnl.RealizedPnlNet / pnl.RealizingTradeCount;

        return new ColumnMetrics(winRate, averagePnl, partition.Count);
    }

    private sealed record ColumnMetrics(decimal? WinRate, decimal? AveragePnl, decimal? TradeCount);
}
