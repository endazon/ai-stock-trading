using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR-13, #1423 M-5, IADR-0582: 費用統制が未結線（CostControl:BaseUrl 未設定）のときの安全既定。常に停止していない（従来どおり LLM を呼ぶ）。
public sealed class NoOpLlmCostHaltGate : ILlmCostHaltGate
{
    public Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
