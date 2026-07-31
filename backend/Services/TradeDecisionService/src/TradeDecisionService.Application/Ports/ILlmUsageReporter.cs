namespace AiStockTrading.TradeDecision.Application.Ports;

// NFR（費用）, FR-04, IADR-0055 決定3: LLM 呼び出しのトークン使用量を費用計測へ渡すポート（計測点は egress）。
// 既定は NoOpLlmUsageReporter（publish しない＝fail-safe）。Worker が PublishingLlmUsageReporter を配線する。
// egress（HttpLlmCompletionClient）とメッセージングを疎結合に保つための境界。
public interface ILlmUsageReporter
{
    Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default);
}

// LLM 応答のトークン使用量（費用算出の入力）。
// IADR-0122 決定1: Model は**ゲートウェイが実際に選択したモデル**（応答の報告値）。単価解決の唯一の根拠であり、
// 要求側の希望モデル（Decision:PrimaryModel 等）は使わない（越境ルーティングで別モデルへ着地し得るため）。
// 既定 null＝モデル不明。計上側は安全側（過小計上を避ける側）へ倒す。
public readonly record struct LlmUsage(int InputTokens, int OutputTokens, string? Model = null);
