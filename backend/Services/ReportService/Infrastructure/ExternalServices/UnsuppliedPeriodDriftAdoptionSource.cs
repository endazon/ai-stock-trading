using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-11, ADR-0041 決定 1, #870, IADR-0360 決定 2: リスク管理サービスの所在が構成されていないときの既定。
// **常に null（未供給）**。
//
// 🔴 **空列へ倒さない。** 空列は「該当なし」であり、取り込みは本番で実際に起き得るため嘘になる
//（`UnsuppliedOpenPositionSource` と同じ向き。`NoOpPeriodFillSource` の「空列＝約定 0 件」とは向きが違う）。
// 加えて、空列へ倒すと在庫の畳み込みからも黙って落ち、**実在しない建玉の評価損益**が出る。
public sealed class UnsuppliedPeriodDriftAdoptionSource : IPeriodDriftAdoptionSource
{
    public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>(null);
}
