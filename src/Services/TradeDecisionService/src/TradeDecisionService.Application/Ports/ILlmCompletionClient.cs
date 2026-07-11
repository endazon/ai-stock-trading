namespace AiStockTrading.TradeDecision.Application.Ports;

// FR-04, ADR-0003, IADR-0017, IADR-0037: LLM 補完のポート。実運用は platform LLM ゲートウェイ（POST /complete）を呼ぶ HTTP 実装
// （後続）。CI は fake で検証する。プロンプトの構築・構造化解析は判断サービス内（PromptBuilder/Parser）に置く。
// IADR-0037: model はモデル識別子（一次=軽量スクリーニング／二次=高性能本判断）。実解決はゲートウェイの構成に委ねる（既定 null）。
public interface ILlmCompletionClient
{
    Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken cancellationToken = default);
}
