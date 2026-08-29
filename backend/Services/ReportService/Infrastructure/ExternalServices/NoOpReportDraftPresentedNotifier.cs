using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-09, IADR-0116, #280: 提示通知の安全既定（no-op）。通知を無効化した構成・単体実行ではこれが選ばれ、
// イベントは 1 件も発行されない（#210 の NoOp 通知ポートと同型）。
public sealed class NoOpReportDraftPresentedNotifier : IReportDraftPresentedNotifier
{
    public Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
