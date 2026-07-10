namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-04, ADR-0003, IADR-0017: LLM 補完のポート。実運用は platform LLM ゲートウェイ（POST /complete）を呼ぶ HTTP 実装
// （後続）。CI は fake で検証する。プロンプトの構築・構造化解析は判断サービス内（PromptBuilder/Parser）に置く。
public interface ILlmCompletionClient
{
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}
