using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using MarketMonitorService.Domain;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03: 1 巡回の判定結果。発行すべきイベント群を保持する。実際の発行（メッセージング）は Worker（Slice B）が行う。
public record MonitorRoundResult(
    IReadOnlyList<StopLossTriggered> StopLosses,
    IReadOnlyList<PriceMovementDetected> PriceMovements)
{
    /// <summary>
    /// FR-10, #902, IADR-0365 決定1: この巡回で評価した保有ポジションごとの記録（価格が取れなかった保有も含む）。
    /// 生存要約（StopLossLivenessReporter）だけが読む。到達の判定・発行には使わない。
    /// </summary>
    public IReadOnlyList<StopLossEvaluation> StopLossEvaluations { get; init; } = [];

    /// <summary>
    /// FR-03, FR-10, #909, IADR-0380 決定2・決定3: この巡回で**市場が閉場していたため評価しなかった**保有ポジション。
    /// <see cref="StopLossEvaluation.Price"/> は常に <c>null</c> である（価格照会そのものを行っていない。
    /// 「照会したが取れなかった」＝<see cref="StopLossEvaluations"/> 側の欠落とは別の事実）。
    /// 保護の空白を声に出すため（StopLossLivenessReporter）だけに使う。到達の判定・発行には使わない。
    /// </summary>
    public IReadOnlyList<StopLossEvaluation> ClosedMarketPositions { get; init; } = [];

    /// <summary>
    /// FR-01, #1132, IADR-0477: この巡回の照会の対象の市場。
    /// #1189, IADR-0494: 保有と監視銘柄の（銘柄・市場）の和集合（重なる銘柄は 1 巡回に 1 回だけ照会するので 1 件）。
    /// <b>閉場中で照会を飛ばした銘柄も含める</b>（日次要求見積りは開場中の量であり、照会した数で数えると、ある市場だけ
    /// 閉じた巡回で値が落ちる）。日次要求見積りの記録（FinnhubDailyVolumeRecorder）だけが読む。
    /// </summary>
    public IReadOnlyList<Market> QuotedSymbolMarkets { get; init; } = [];
}
