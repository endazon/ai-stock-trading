namespace AiStockTrading.TradeDecision.Application.Ports;

// NFR（費用）, FR-04, IADR-0055 決定3: LLM 呼び出しのトークン使用量を費用計測へ渡すポート（計測点は egress）。
// 既定は NoOpLlmUsageReporter（publish しない＝fail-safe）。Worker が PublishingLlmUsageReporter を配線する。
// egress（HttpLlmCompletionClient）とメッセージングを疎結合に保つための境界。
public interface ILlmUsageReporter
{
    Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default);
}

// LLM 応答のトークン使用量（費用算出の入力）。
public readonly record struct LlmUsage(int InputTokens, int OutputTokens);
