using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Notification.Infrastructure.Composable.Steps;

// FR-09, UC-01, UC-02, UC-06: 取引実行・リスク統制発動のイベントを購読し、テンプレート整形して通知するハンドラ群。
// 送信失敗（実 Discord 送信時）は例外化され、メッセージングの再試行（2s/10s/30s）→ <queue>_error で
// 回復性を担保する（IADR-0020・IADR-0129 決定 5）。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<T> から Wolverine のハンドラへ移行した。
// ConsumeContext<T> は消え、メッセージ本体と CancellationToken をメソッド引数で受け取る。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする（Wolverine は public でない型を受け付けない）。
// 本サービスは購読専用（発行 0）であり、11 種のイベントそれぞれに 1 本ずつキューを持つ
// （ai-stock-trading.notification-service.<イベント型名>）。

public sealed class OrderExecutedNotificationHandler(INotificationSender sender)
{
    public Task Handle(OrderExecuted message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

public sealed class OrderRejectedNotificationHandler(INotificationSender sender)
{
    public Task Handle(OrderRejected message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

public sealed class StopLossTriggeredNotificationHandler(INotificationSender sender)
{
    public Task Handle(StopLossTriggered message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-17, UC-06: 全体前提条件の変更（設定管理サービス #19）を購読して通知する。
public sealed class AssumptionsChangedNotificationHandler(INotificationSender sender)
{
    public Task Handle(AssumptionsChanged message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-07, FR-09: 報告書の確定（報告書サービス #14）を購読して通知する。
public sealed class ReportConfirmedNotificationHandler(INotificationSender sender)
{
    public Task Handle(ReportConfirmed message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-06/07/09, UC-03〜05, IADR-0116, #280: 報告書ドラフトの提示（報告書サービスの自動生成スケジューラ）を購読し、
// 要約と確定依頼を通知する。Discord 未設定なら送信ポートが no-op に倒れる（IADR-0020/0062）＝送信経路の追加のみ。
public sealed class ReportDraftPresentedNotificationHandler(INotificationSender sender)
{
    public Task Handle(ReportDraftPresented message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// NFR（費用）, FR-09: 費用しきい値到達（費用統制 #23）を購読して通知する。
public sealed class CostThresholdReachedNotificationHandler(INotificationSender sender)
{
    public Task Handle(CostThresholdReached message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-20, FR-09, UC-06: 撤退基準到達（撤退の定期評価ドライバ #166）を購読して利用者へ通知する。
public sealed class WithdrawalTriggeredNotificationHandler(INotificationSender sender)
{
    public Task Handle(WithdrawalTriggered message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// UC-01, FR-09, FR-07, #210: 日報未確定による取引スキップ（取引判断 #11）を購読して確定を促す通知を送る。
public sealed class DailyPolicyUnconfirmedNotificationHandler(INotificationSender sender)
{
    public Task Handle(DailyPolicyUnconfirmed message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-05, FR-09, FR-10, #292, IADR-0118: 取引台帳とブローカ実ポジションの乖離を購読して通知する。
// 是正は行わないため、通知が利用者へ届くことが検知の唯一の出口になる。
public sealed class PositionReconciliationDriftNotificationHandler(INotificationSender sender)
{
    public Task Handle(PositionReconciliationDrift message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-09, FR-10, FR-11, UC-06, ADR-0016 決定4（2026-08-06 改訂）, #419, IADR-0159:
// 強制買戻しの**推定**を購読して利用者へ通知する。推定である以上、運用者が事後に検証できることが要件であり、
// 通知はその第一の出口である（文言で「推定」と明示する責務は NotificationFormatter が持つ）。
public sealed class BuyInInferredNotificationHandler(INotificationSender sender)
{
    public Task Handle(BuyInInferred message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-09, FR-10, UC-06, #330, IADR-0133: 維持率割れによる建玉の自動縮小を購読して利用者へ通知する。
// 利用者の承認を待たずに決済するため、通知が「建玉が減ったこと」を利用者が知る最初の経路になる。
public sealed class MaintenanceMarginReductionExecutedNotificationHandler(INotificationSender sender)
{
    public Task Handle(MaintenanceMarginReductionExecuted message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-10, FR-17, #381, ADR-0022 決定2・決定5, IADR-0196: 為替の情報源の劣化を Discord へ通知する 3 本。
// 「黙って劣化させない」——ログだけでは運用者が能動的に見に行かない限り気づけない。
public sealed class FxRateSourceFellBackNotificationHandler(INotificationSender sender)
{
    public Task Handle(FxRateSourceFellBack message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

public sealed class FxRateSourcePrimaryRestoredNotificationHandler(INotificationSender sender)
{
    public Task Handle(FxRateSourcePrimaryRestored message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

public sealed class FxRateStaleNotificationHandler(INotificationSender sender)
{
    public Task Handle(FxRateStale message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-10, FR-09, #381, IADR-0198: 鮮度切れのレートで決済した事実を Discord へ通知する。
public sealed class PositionClosedWithStaleFxRateNotificationHandler(INotificationSender sender)
{
    public Task Handle(PositionClosedWithStaleFxRate message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-04, FR-06, FR-09, ADR-0017 決定4-(2), #335, IADR-0217: フォールバック発火を Discord へ警告通知する
// （可視化 3 経路の②）。**埋もれない経路で出す**ことが決定の目的である。
public sealed class LlmFallbackFiredNotificationHandler(INotificationSender sender)
{
    public Task Handle(LlmFallbackFired message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}

// FR-04, FR-09, UC-01, ADR-0017 決定2, #335, IADR-0216: 割当モデル不可による取引判断の見送りを通知する。
// **沈黙のスキップにしない**（同決定2）。障害ではなく設計上の正常な結果であることは文言側が担う。
public sealed class TradeDecisionSkippedNotificationHandler(INotificationSender sender)
{
    public Task Handle(TradeDecisionSkipped message, CancellationToken cancellationToken) =>
        sender.SendAsync(NotificationFormatter.From(message), cancellationToken);
}
