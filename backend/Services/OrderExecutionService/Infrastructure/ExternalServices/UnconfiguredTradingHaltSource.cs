using Microsoft.Extensions.Logging;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// 🔴 FR-10, NFR-04, IADR-0565 決定 2: 照会先（RiskManagement:BaseUrl）が構成されていないときの停止の状態。**常に不明**であり、
// 新規建ては送られない（fail-closed）。決済・損切りは照会しないので影響を受けない。照会を省く既定・構成の口は持たない。
public sealed class UnconfiguredTradingHaltSource(ILogger<UnconfiguredTradingHaltSource> logger) : ITradingHaltSource
{
    public Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default)
    {
        logger.LogError(
            "停止の状態（kill switch・一時停止）の照会先 RiskManagement:BaseUrl が構成されていません。"
            + "不明として扱い、新規建てを送りません（決済・損切りは止めません）。");
        return Task.FromResult<TradingHaltState?>(null);
    }
}
