using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// 🔴 FR-10, FR-05, FR-11, UC-06, #1105, IADR-0461 決定2・決定3: 発注執行が決済（Close）の数量を、
// **同じ建玉を売る処理中の決済の分だけ縮めて送った**事実。
//
// - ブローカーの建玉（`Position.Qty`）は、約定していない売り注文が押さえている株数を引かない。発注執行が自分で出した
//   処理中の決済（S1 の成行決済など。S0 / S3 の保護レグは含まない）を引かないと、証券会社が「建玉が足りない」で拒否する
//   （2026-09-29 の PoC で実測）。
// - **台帳の乖離ではない**ので乖離イベント（`PositionReconciliationDrift`）は出さない。監査台帳だけが購読する
//   （利用者の対処は要らない。黙って数量を変えないために記録する）。
// - 1 株も残らないときは本イベントではなく見送り（`OrderDispatchForgone`・理由 `InFlightCloseCoversPosition`）になる。
// - `ApprovedQuantity` は承認が運んだ数量、`BrokerClosableQuantity` はブローカーの決済方向の建玉、
//   `InFlightQuantity` は引いた処理中の株数、`DispatchedQuantity` は実際に送った株数。
//   台帳の乖離でも縮めた回（`PositionReconciliationDrift` も出る）では、送った株数は両方を引いた値である。
// - `InFlightDecisionIds` は引いた処理中の決済の DecisionId（監査台帳でその決済と突き合わせるため）。
public record CloseReducedForInFlightCloses(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int ApprovedQuantity,
    int BrokerClosableQuantity,
    int InFlightQuantity,
    int DispatchedQuantity,
    IReadOnlyList<Guid> InFlightDecisionIds,
    DateTimeOffset OccurredAt);
