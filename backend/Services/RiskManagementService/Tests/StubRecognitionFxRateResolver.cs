using RiskManagementService.Features.RiskManagement;

namespace RiskManagementService.Tests;

// #611, IADR-0286 決定1: 承認ハンドラのテストで認識時レート（1 USD あたりの円）を固定値で与える最小の偽装。
// 既定 null＝未記録（為替レート源が無い構成と同じ）。解決の規則そのものは FxSourceRecognitionFxRateResolverTests が見る。
internal sealed class StubRecognitionFxRateResolver(decimal? rate = null) : IRecognitionFxRateResolver
{
    public int Calls { get; private set; }

    public Task<decimal?> ResolveBaseToDisplayAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(rate);
    }
}
