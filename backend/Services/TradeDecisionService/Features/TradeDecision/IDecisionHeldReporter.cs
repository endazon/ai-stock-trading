using AiStockTrading.Shared.Contracts.Events;

namespace TradeDecisionService.Features.TradeDecision;

// UC-02, FR-03, #1077, IADR-0451 決定4: AI 判断が結論を出したのに発注意図を作らなかった（見送った）ことを、
// 判断時点の価格つきで市場監視（急変の基準値）へ渡すポート。既定は NoOpDecisionHeldReporter（publish しない）で、
// Worker が PublishingDecisionHeldReporter を配線する（IScreeningReductionReporter と同じ作法: 省略可能・既定 NoOp）。
//
// 🔴 **配線しないと UC-02 は Hold が続く間発火しない**（#1077 の症状そのもの）。本番の Program.cs は必ず配線する。
public interface IDecisionHeldReporter
{
    Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default);
}
