using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-16, #563, IADR-0268: リスク管理サービスの所在が構成されていないときの既定。**常に null（未供給）**。
//
// 🔴 **空列へ倒さない。** 空列は「建玉なし」であり、**建玉は本番で実際に存在し得る**ため嘘になる
//（`NoOpPeriodFillSource` の「空列＝約定 0 件」とは向きが違う。約定 0 件は §1 サマリの取引回数 0 と整合するが、
//  建玉 0 件は「今は何も持っていない」という別の主張になる）。
public sealed class UnsuppliedOpenPositionSource : IOpenPositionSource
{
    public Task<IReadOnlyList<ReportPosition>?> GetOpenPositionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ReportPosition>?>(null);
}
