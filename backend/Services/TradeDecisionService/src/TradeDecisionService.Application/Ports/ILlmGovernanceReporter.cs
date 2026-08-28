using AiStockTrading.Shared.Contracts.Llm;

namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-04, FR-09, FR-11, ADR-0017 決定2/決定4, #335, IADR-0216/0217:
// LLM の割当統制に関する事実（フォールバック発火・取引判断のスキップ）を外へ出すポート。
// 既定は NoOpLlmGovernanceReporter（publish しない＝fail-safe）。Worker が発行実装を配線する。
// egress（HttpLlmCompletionClient）とメッセージングを疎結合に保つための境界であり、ILlmUsageReporter と同型である。
//
// 🔴 **記録・通知は「後から足す装飾」ではなく決定の一部である**（ADR-0017 決定2/決定4）。
// 沈黙のスキップ・沈黙のフォールバックを作らないことが目的であり、実装が無いと
// 「動いているように見える失敗」が発見されない。
public interface ILlmGovernanceReporter
{
    /// <summary>ピン以外のモデルが応答した（フォールバック発火・未割当・禁止モデル）。</summary>
    Task FallbackFiredAsync(LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default);

    /// <summary>モデルが利用できないため取引判断を実行しなかった（発注も行わない）。</summary>
    Task DecisionSkippedAsync(
        string purpose, string reason, string? expectedModel, string? effectiveModel,
        CancellationToken cancellationToken = default);
}
