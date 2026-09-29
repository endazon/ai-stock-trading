using AiStockTrading.Shared.Contracts.Events;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 NFR, FR-04, FR-11, #1092, IADR-0462 決定4: LLM を呼ぶ前の見送り（4 地点）を監査台帳へ渡すポート。
// 既定は NoOpDecisionForgoneBeforeLlmReporter（publish しない）で、Worker が PublishingDecisionForgoneBeforeLlmReporter を配線する
// （IDecisionHeldReporter と同じ作法: 省略可能・既定 NoOp）。
//
// 🔴 **配線しないと、LLM を呼ぶ前の見送りは従来どおりメトリクスとログにしか残らず、Pod の再起動で消える**（#1092 の症状）。
public interface IDecisionForgoneBeforeLlmReporter
{
    Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default);
}
