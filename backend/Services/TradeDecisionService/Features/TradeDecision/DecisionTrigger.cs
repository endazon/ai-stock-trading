using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, FR-03, IADR-0023: 判断の起点を一般化したトリガー。定時（Scheduled）と価格変動（PriceMovement）の 2 系統を
// 同一の DecideAsync へ合流させる。価格文脈（Price/BaselinePrice/ChangeRatio）は PriceMovement 起点でのみ与えられる。
public enum DecisionTriggerKind
{
    Scheduled,
    PriceMovement,
}

public sealed record DecisionTrigger(
    string Symbol,
    Market Market,
    DecisionTriggerKind Kind,
    decimal? Price = null,
    decimal? BaselinePrice = null,
    decimal? ChangeRatio = null,
    // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点イベントが発生した時刻
    // （PriceMovementDetected.DetectedAt / InformationCollected.CollectedAt）。
    // 端点間レイテンシの t0 であり、判断が下流へ運ぶ provenance の素になる。
    // **判断サービスの現在時刻で代用しない**——それでは LLM 判断より前の区間（検知・配送）が消える。
    DateTimeOffset? CycleStartedAt = null)
{
    // NFR-01, NFR-02, #689: メトリクスの trigger タグ値（既存の業務メトリクスと同じ語彙を使う）。
    public string MetricTrigger => Kind == DecisionTriggerKind.PriceMovement
        ? BusinessMetrics.TriggerPriceMovement
        : BusinessMetrics.TriggerScheduled;

    // 価格変動イベント（イベント駆動系統）から生成する。
    public static DecisionTrigger FromPriceMovement(PriceMovementDetected e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new DecisionTrigger(
            e.Symbol, e.Market, DecisionTriggerKind.PriceMovement, e.Price, e.BaselinePrice, e.ChangeRatio, e.DetectedAt);
    }

    // 定時サイクル（価格変動トリガーなし）から生成する。銘柄・市場は監視銘柄（watchlist）由来。
    // cycleStartedAt は起点の InformationCollected.CollectedAt（供給しなければ端点間は未観測になる）。
    public static DecisionTrigger Scheduled(string symbol, Market market, DateTimeOffset? cycleStartedAt = null) =>
        new(symbol, market, DecisionTriggerKind.Scheduled, CycleStartedAt: cycleStartedAt);
}
