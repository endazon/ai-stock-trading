using ReportService.Features.Reports;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-20, #569, IADR-0271: 権威源へ結線されていない構成の安全既定。**常に null（未供給）**。
//
// 🔴 Stage 0 を既定にしない。「Stage 0 ＝どの段にも到達していない」は**主張**であり、
// 未結線の環境で三者比較が全列空欄になっても読み手はそれを事実と読む。
public sealed class UnsuppliedStageProgressSource : IStageProgressSource
{
    public Task<TradingStage?> GetCurrentStageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<TradingStage?>(null);
}
