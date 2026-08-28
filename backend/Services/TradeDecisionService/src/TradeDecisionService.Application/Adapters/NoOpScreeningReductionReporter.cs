using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TradeDecision.Application.Ports;

namespace AiStockTrading.TradeDecision.Application.Adapters;

// #337, IADR-0247: 既定の no-op（publish しない）。縮退制御が無効（予算未設定）の既定構成では
// 縮退自体が起きないため、本実装が呼ばれることはない。実発行（PublishingScreeningReductionReporter）は
// Worker が配線する。
public sealed class NoOpScreeningReductionReporter : IScreeningReductionReporter
{
    public Task ReportAsync(ScreeningContextReduced reduction, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
