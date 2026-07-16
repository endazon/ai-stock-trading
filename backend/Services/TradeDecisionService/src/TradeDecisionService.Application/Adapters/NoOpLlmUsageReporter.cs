using AiStockTrading.TradeDecision.Application.Ports;

namespace AiStockTrading.TradeDecision.Application.Adapters;

// NFR（費用）, IADR-0055 決定3: 費用計測の安全既定。外部へ publish せず何もしない。
// 実計測（LlmCostIncurred publish）は Worker が PublishingLlmUsageReporter を明示配線したときのみ有効になる。
public sealed class NoOpLlmUsageReporter : ILlmUsageReporter
{
    public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
