using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-16, FR-11, #563, IADR-0268: 監査台帳の所在が構成されていないときの既定。**常に null（未供給）**を返す。
//
// 🔴 **空の辞書へ倒さない。** 空は「引けたが根拠の記録が 1 件も無い」であり、
// **判断根拠は本番で実際に記録されている**ため、空を既定にすると端的に嘘になる
//（`UnsuppliedFxSourceStatusSource` と同じ向き。`NoOpPeriodFillSource` の「空列へ倒す」とは逆である）。
public sealed class UnsuppliedTradeRationaleSource : ITradeRationaleSource
{
    public Task<IReadOnlyDictionary<Guid, string>?> GetRationalesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>?>(null);
}
