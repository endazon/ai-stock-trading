using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-19, #154, IADR-0067: 注文アクティビティの射影ストア（相場操縦検知の入力源）。
// 注文系イベント（OrderApproved/OrderExecuted/OrderModified/OrderCancelled）を Risk 専有 DB へ射影し、
// EfOrderActivitySource（IOrderActivitySource）が窓を読む。射影は DecisionId をキーに冪等（再送に耐える）。
//
// 承認（OrderApproved）だけが銘柄・方向を持つため、行の生成は承認で行い、以降の約定・訂正・取消は DecisionId で
// 既存行を更新する（OrderExecuted と同じ相関補完の設計・IADR-0018）。相関する承認が無いイベントは無視する。
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
