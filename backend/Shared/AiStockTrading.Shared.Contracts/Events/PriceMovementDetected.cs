using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-03, UC-02, IADR-0014: 市場監視が監視銘柄の変動率が閾値を超えたことを検知した。
// 取引判断サービス／サイクルが購読し、対象銘柄に限定した取引サイクルを即時起動する。
// ChangeRatio = (Price − BaselinePrice) / BaselinePrice。BaselinePrice は前回 AI 判断時点の価格。
public record PriceMovementDetected(
    Guid EventId,
    string Symbol,
    Market Market,
    decimal Price,
    decimal BaselinePrice,
    decimal ChangeRatio,
    DateTimeOffset DetectedAt);
