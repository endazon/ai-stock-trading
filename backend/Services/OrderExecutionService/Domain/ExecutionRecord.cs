using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-05, FR-16: 発注結果の記録（注文実体＋スリッページ）。月報レビュー（FR-16）・ポジション/損益射影のデータ源。
// DecisionId で取引判断/損切りと相関する。SlippageRatio は「不利＝正」（SlippageCalculator）。
// 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定5・決定6: StopFloorSource は新規建ての承認の発注意図が運んだ「損切り幅に下限を掛けて
// ラインを引いた」印（OrderIntent.StopFloorSource。Fallback2Pct / Atr14）。null＝分からない（#1122 より前の記録・決済・保護レグ）。
// 既存の S1 への下限の遡及（SoftwareStopFloorRetrofitter）が、印のある行を広げないために読む。突合で確定した記録は予約の行が残した印を使う。
// 🔴 FR-10, UC-06, ADR-0050 決定1, #1222, IADR-0515 決定2: ApprovalOrigin は承認の経路の発注が承認から写した出どころ（判断・利用者の手仕舞い・
// 維持率割れの自動縮小）。null＝分からない（#1222 より前の記録・保護の機構の記録・突合で確定した記録・出どころを持たない旧い承認）。
// S1 の決済の前の取消（SoftwareStopExecutor）が、利用者・自動縮小の決済を取り消さず差し引くために読む。null は判断の手仕舞いと同じく取り消す側。
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
    StopWidthFloorSource? StopFloorSource = null,
    OrderApprovalOrigin? ApprovalOrigin = null);
