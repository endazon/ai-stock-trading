using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Tests;

// NFR, FR-04, FR-11, #1111, IADR-0483: 取引判断の最中の例外の最終の失敗の報告を記録する試験用の報告口。
// ハンドラの必須依存であるため、ハンドラを組む Wolverine の試験ホストはこれ（か本番の実装）を登録する。
public sealed class RecordingTradeDecisionFailureReporter : ITradeDecisionFailureReporter
{
    public sealed record Call(string CycleTrigger, string Symbol, Market Market, Exception Exception);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public Task ReportFinalFailureAsync(string cycleTrigger, string symbol, Market market, Exception exception)
    {
        Calls.Enqueue(new Call(cycleTrigger, symbol, market, exception));
        return Task.CompletedTask;
    }
}
