using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;

namespace AiStockTrading.Report.Worker.Composable.Adapters;

// FR-06/07/09, IADR-0116 決定1/2, #280: 提示（確定依頼）を ReportDraftPresented としてバスへ発行する。
// 通知サービスが購読して Discord へ投稿し、監査サービスが中央台帳へ集約する。
//
// 常駐（BackgroundService）の巡回はスコープを作って呼ぶが、発行そのものはスコープに依存しないため singleton の
// IBus を用いる（scoped な IPublishEndpoint を常駐から直接引かない）。失敗の握り潰しは呼び出し側
// （ReportAutoGenerator）が行い、ここでは素直に投げる（通知経路の異常を隠さない）。
internal sealed class MassTransitReportDraftPresentedNotifier(IBus bus, IClock clock)
    : IReportDraftPresentedNotifier
{
    public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return bus.Publish(
            new ReportDraftPresented(
                notice.PeriodKey,
                notice.Kind.ToString(),
                notice.PeriodLabel,
                notice.Summary,
                notice.Version,
                clock.UtcNow),
            cancellationToken);
    }
}
