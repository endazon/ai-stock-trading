using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Observability;

namespace OrderExecutionService.Tests;

// NFR, FR-10, #1092, IADR-0462: 建玉照会の成功・失敗の報告を記録する偽物（業務クラスが「どの発生源で・何を」報告したかの観測点）。
internal sealed class RecordingPositionQueryHealth : IPositionQueryHealthReporter
{
    public List<(PositionQuerySource Source, bool Succeeded, string? FailureKind)> Reports { get; } = [];

    public Task ReportAsync(PositionQuerySource source, bool succeeded, string? failureKind = null)
    {
        lock (Reports)
            Reports.Add((source, succeeded, failureKind));
        return Task.CompletedTask;
    }
}
