using AiStockTrading.Shared.Contracts.Observability;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// #891, IADR-0374: 既定の no-op（計上しない）。単体テストが判断サービスを直接組む場合の既定であり、
// **本番では配線されない**（Program.cs が MetricsDecisionSkipReporter を登録する）。
public sealed class NoOpDecisionSkipReporter : IDecisionSkipReporter
{
    public void Report(string trigger, DecisionSkipReason reason)
    {
    }
}

// FR-04, FR-10, NFR-07, #891, IADR-0374: 見送りを業務メトリクスへ計上する実装。
// ast.trade_cycle.decision_skips{reason,trigger} を 1 件足すだけであり、I/O も状態も持たない。
public sealed class MetricsDecisionSkipReporter(BusinessMetrics metrics) : IDecisionSkipReporter
{
    public void Report(string trigger, DecisionSkipReason reason) =>
        metrics.RecordTradeDecisionSkipped(trigger, reason);
}
