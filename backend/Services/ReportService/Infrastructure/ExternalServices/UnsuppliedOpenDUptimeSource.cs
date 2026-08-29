using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-20, #569, IADR-0271: 権威源へ結線されていない構成の安全既定。**常に null（未供給）**。
//
// 🔴 空の OpenDUptimeRecord（＝観測された取引日が 1 日も無い）を返さない。
// 空列は月報の分布で「100%: 0 日 / 50〜99%: 0 日 / 50% 未満: 0 日」となり、
// **稼働の記録が取れていないことと、稼働した日が 1 日も無かったことが区別できなくなる。**
public sealed class UnsuppliedOpenDUptimeSource : IOpenDUptimeSource
{
    public Task<OpenDUptimeRecord?> GetUptimeAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        Task.FromResult<OpenDUptimeRecord?>(null);
}
