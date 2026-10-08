using AiStockTrading.Shared.Contracts.Llm;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-09, FR-11, #1267, IADR-0517: LLM ゲートウェイの送信の成否（Sent=false / Sent=true）を受け、
// **Sent=false の連続**を運用者へ知らせるポート。既定は NoOpLlmGatewayUnsentNotifier（何もしない）。
// Worker が PublishingLlmGatewayUnsentNotifier（singleton。連続の状態を呼び出しを跨いで持つ）を配線する。
//
// 🔴 **数えるのは「応答が届いて Sent=false」だけである。** 非 2xx・タイムアウト・応答不正は別の Hold の系統であり
// （IADR-0104 決定3 / IADR-0216 / IADR-0323）、連続を進めも戻しもしない。
// 🔴 本ポートは可観測性であって統制ではない。Hold へ倒す判断は HttpLlmCompletionClient が本ポートと独立に行う。
public interface ILlmGatewayUnsentNotifier
{
    /// <summary>応答が届き Sent=false だった（原因はゲートウェイの申告のまま）。</summary>
    Task ReportUnsentAsync(string purpose, LlmGatewayUnsentCause cause, CancellationToken cancellationToken = default);

    /// <summary>応答が届き Sent=true だった（連続が途切れた）。</summary>
    Task ReportSentAsync(CancellationToken cancellationToken = default);
}
