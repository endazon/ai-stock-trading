using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-05, FR-16: 発注結果の記録（注文実体＋スリッページ）。月報レビュー（FR-16）・ポジション/損益射影のデータ源。
// DecisionId で取引判断/損切りと相関する。SlippageRatio は「不利＝正」（SlippageCalculator）。
// 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定5・決定6: StopFloorSource は新規建ての承認の発注意図が運んだ「損切り幅に下限を掛けて
// ラインを引いた」印（OrderIntent.StopFloorSource。Fallback2Pct / Atr14）。null＝分からない（#1122 より前の記録・決済・保護レグ）。
// 既存の S1 への下限の遡及（SoftwareStopFloorRetrofitter）が、印のある行を広げないために読む。突合で確定した記録は予約の行が残した印を使う。
public record ExecutionRecord(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    PositionEffect PositionEffect,
    int Quantity,
    decimal PlannedPrice,
    int FilledQuantity,
    decimal AveragePrice,
    OrderStatus Status,
    decimal SlippageRatio,
    DateTimeOffset ExecutedAt,
    StopWidthFloorSource? StopFloorSource = null);
