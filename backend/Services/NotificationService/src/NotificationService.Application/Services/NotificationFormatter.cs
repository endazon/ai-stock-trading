using AiStockTrading.Notification.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Notification.Application.Services;

// FR-09, UC-01, UC-02, UC-06: ドメインイベントを種別ごとのテンプレートで NotificationMessage に整形する純関数群。
public static class NotificationFormatter
{
    // 取引実行（約定）。全量約定は Info、それ以外（拒否・取消等）は注意喚起の Warning。
    public static NotificationMessage From(OrderExecuted e) => new(
        "取引実行",
        $"約定 {e.Status} 数量{e.FilledQuantity}@{e.AveragePrice}（OrderId={e.OrderId}・DecisionId={e.DecisionId}）",
        e.Status == OrderStatus.Filled ? NotificationSeverity.Info : NotificationSeverity.Warning);

    // リスク統制発動: 発注拒否（理由つき）。
    public static NotificationMessage From(OrderRejected e) => new(
        "リスク統制: 発注拒否",
        $"{e.Intent.Symbol} 拒否: {string.Join(",", e.Reasons)}（DecisionId={e.DecisionId}）",
        NotificationSeverity.Warning);

    // リスク統制発動: 損切りライン到達（機械執行の起点）。
    public static NotificationMessage From(StopLossTriggered e) => new(
        "リスク統制: 損切りライン到達",
        $"{e.Symbol} 損切り SL={e.StopLossPrice}（現在 {e.Price}・数量 {e.Quantity}・建玉 {e.PositionSide}）",
        NotificationSeverity.Critical);

    // FR-17: 全体前提条件の変更（利用者による設定変更の通知）。
    public static NotificationMessage From(AssumptionsChanged e) => new(
        "設定変更: 全体前提条件",
        $"前提条件が更新されました（v{e.Version}・{e.Actor}）: {e.Reason}",
        NotificationSeverity.Info);

    // FR-07, FR-09: 報告書の確定（方針が取引に有効化された通知）。
    public static NotificationMessage From(ReportConfirmed e) => new(
        "報告書確定",
        $"{e.Kind} 報告書 {e.PeriodKey} が確定しました（{e.Actor}・前提条件 v{e.AssumptionsVersion}）。",
        NotificationSeverity.Info);

    // NFR（費用）, FR-09: 費用しきい値到達（間隔延長/停止）。停止（Halted）は Critical、間隔延長（Throttled）は Warning。
    public static NotificationMessage From(CostThresholdReached e) => new(
        $"費用統制: {e.State}",
        $"{e.Category} 費用が月次上限の {e.Percent:F0}% に到達しました（{e.Month}・{e.State}）。",
        e.State == "Halted" ? NotificationSeverity.Critical : NotificationSeverity.Warning);

    // FR-20, FR-09, UC-06, #166: 撤退基準到達（自動安全側の発火）。新規建ての自動停止を伴う撤退は Critical。
    // 段階の実降格は提案に留まる（確定は利用者承認による差し戻しを要する）ことを本文で明示する。
    public static NotificationMessage From(WithdrawalTriggered e) => new(
        "リスク統制: 撤退基準到達",
        $"撤退基準に到達しました（{e.Reason}）。"
            + $"{(e.HaltNewEntries ? "新規建てを自動停止しました。" : string.Empty)}"
            + $"Stage {e.ProposedStage} への差し戻しを提案します（確定は利用者承認が必要）。",
        e.HaltNewEntries ? NotificationSeverity.Critical : NotificationSeverity.Warning);
}
