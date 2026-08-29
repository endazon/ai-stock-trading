using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-20, #569, INDEX 決定34, 04_report-templates 日報 §1 / 月報 §6.2, IADR-0271:
// 期間の OpenD 稼働率の供給（権威源はリスク管理サービスの稼働観測ログ）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 「稼働率 0%」は終日停止という重い事実であり、
// 「照会できていない」とは別物である（IADR-0250 決定1 の規律）。
public interface IOpenDUptimeSource
{
    /// <summary>取引日（米国東部時間）[from, to] の稼働率。照会できなければ null（未供給）。</summary>
    Task<OpenDUptimeRecord?> GetUptimeAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
}
