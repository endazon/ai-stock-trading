using TradeDecisionService.Application.Ports;

namespace TradeDecisionService.Application.Adapters;

// UC-01, FR-09, IADR-0096 決定2: 日報未確定通知の安全既定。何もしない（＝現行のログのみ＝通知しない）。
// 実発行（DailyPolicyUnconfirmed の publish・営業日 dedup）は Worker が構成フラグ
// TradeCycle:NotifyOnUnconfirmedPolicy=true のときだけ PublishingDailyPolicyUnconfirmedNotifier へ差し替える。
public sealed class NoOpDailyPolicyUnconfirmedNotifier : IDailyPolicyUnconfirmedNotifier
{
    public Task NotifyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
