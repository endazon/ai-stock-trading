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
}
