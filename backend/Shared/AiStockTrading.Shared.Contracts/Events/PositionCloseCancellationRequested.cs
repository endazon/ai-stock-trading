using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-10, FR-11, UC-06, ADR-0003, #847, #768, IADR-0357: 利用者（owner）が**板に残った手仕舞いの取消**を
// 要求した。
//
// 相関は既存の注文系と同じ DecisionId。PositionCloseRequested（手仕舞いの要求）と対になる契約であり、
// 同じく Actor と Reason を運ぶ —— OrderCancelled は**アクターも理由の出所も持たない**ため、これが無いと
// 「誰が・なぜ板の注文を消したか」が監査台帳に残らない（FR-11）。
//
// 受け手は 2 つある。中央監査台帳（AuditService）と、発注執行（OrderExecutionService）である。後者が
// OrderAmendmentDispatcher.CancelAsync を呼ぶ —— これが #768（DI 登録だけで呼び出し元が無い）の配線である。
//
// 🔴 本イベントは「取り消したい」であって「取り消せた」ではない。**在庫の押さえを解く引き金にしてはならない**
// （それは確認できた終端＝OrderCancelled / OrderExecuted(Cancelled) の役目である。IADR-0117 改定 1/4）。
public record PositionCloseCancellationRequested(
    Guid DecisionId,
    string Symbol,
    Market Market,
    string Actor,
    string Reason,
    DateTimeOffset RequestedAt);
