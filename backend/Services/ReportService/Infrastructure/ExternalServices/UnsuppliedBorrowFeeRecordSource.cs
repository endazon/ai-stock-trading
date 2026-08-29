using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, #338, ADR-0027 決定4, IADR-0254: 監査台帳へ結線されていない構成の安全既定。**常に null（未供給）**。
//
// 🔴 空の BorrowFeeRecord（＝空売り建玉なし）を返さない。**空売りは Stage 3 で解禁される**ため、
// 「建玉が無い」を既定にすると、解禁後に結線を忘れた月がそのまま「0 件」で通る。
public sealed class UnsuppliedBorrowFeeRecordSource : IBorrowFeeRecordSource
{
    public Task<BorrowFeeRecord?> GetBorrowFeesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        Task.FromResult<BorrowFeeRecord?>(null);
}
