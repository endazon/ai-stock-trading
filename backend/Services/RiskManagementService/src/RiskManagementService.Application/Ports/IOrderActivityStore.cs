using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.Ports;

// FR-19, #154, IADR-0067: 注文アクティビティの射影ストア（相場操縦検知の入力源）。
// 注文系イベント（OrderApproved/OrderExecuted/OrderModified/OrderCancelled）を Risk 専有 DB へ射影し、
// EfOrderActivitySource（IOrderActivitySource）が窓を読む。射影は DecisionId をキーに冪等（再送に耐える）。
//
// 承認（OrderApproved）だけが銘柄・方向を持つため、行の生成は承認で行い、以降の約定・訂正・取消は DecisionId で
// 既存行を更新する（OrderExecuted と同じ相関補完の設計・IADR-0018）。相関する承認が無いイベントは無視する。
//
// 到着順序について（許容する制約・IADR-0067）: メッセージングは異なるイベント型間の到着順序を保証しないため、
// 稀に約定・訂正・取消が承認の射影より先に処理されると、その更新は反映されずに落ちる。ただし注文は「承認→発注」を
// 経て初めて存在するため実運用の順序では承認が先行し（既存の OrderExecutedLedgerConsumer/AppendFill も同じ前提で
// 承認欠如時にスキップする・IADR-0018）、かつ相場操縦検知は最小標本ガード付きの窓集計のため単発の取りこぼしは
// 無嫌疑側（fail-safe）に倒れる。再順序化バッファは過剰実装として持たない（本 PR は配管まで）。厳密な順序保証が
// 要るなら承認欠如時のバッファリングを別途検討する（IADR-0067 のフォローアップ）。
public interface IOrderActivityStore
{
    /// <summary>承認済み注文を新規の注文アクティビティ行として記録する。既存 DecisionId は無視する（冪等）。</summary>
    void RecordPlacement(Guid decisionId, string symbol, Market market, TradeSide side, int quantity, DateTimeOffset placedAt);

    /// <summary>約定結果で行を更新する（状態・約定数、終端なら終端時刻）。相関する承認が無ければ何もしない。</summary>
    void RecordExecution(Guid decisionId, OrderStatus status, int filledQuantity, DateTimeOffset executedAt);

    /// <summary>訂正で行を更新する（訂正回数 +1・数量を訂正後で更新）。相関する承認が無ければ何もしない。</summary>
    void RecordModification(Guid decisionId, int quantity, DateTimeOffset modifiedAt);

    /// <summary>取消で行を更新する（状態を取消・終端時刻を設定）。相関する承認が無ければ何もしない。</summary>
    void RecordCancellation(Guid decisionId, DateTimeOffset cancelledAt);
}
