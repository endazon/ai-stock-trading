using AiStockTrading.Report.Application.Ports;

namespace AiStockTrading.Report.Application.Adapters;

// NFR（費用）, #347, IADR-0219: 費用計測の安全既定。外部へ publish せず何もしない。
// 実計測（LlmCostIncurred publish）は Worker が発行実装を明示配線したときのみ有効になる。
public sealed class NoOpLlmUsageReporter : ILlmUsageReporter
{
    public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
