namespace TradeDecisionService.Features.TradeDecision;

// FR-04, ADR-0003, IADR-0017, IADR-0039: LLM 補完のポート。実運用は platform LLM ゲートウェイ（POST /complete）を呼ぶ HTTP 実装
// （後続）。CI は fake で検証する。プロンプトの構築・構造化解析は判断サービス内（PromptBuilder/Parser）に置く。
// IADR-0039: model はモデル識別子（一次=軽量スクリーニング／二次=高性能本判断）。実解決はゲートウェイの構成に委ねる（既定 null）。
// FR-04, ADR-0014, ADR-0017, #335, IADR-0212: purpose は**呼び出しごと**の用途キー（`LlmPurposes.*`）である。
// 二段判断は層ごとに別の用途（一次=trade-decision-screening／二次=trade-decision）を持ち、割当モデルも
// 費用の計上区分もそこで分かれる。**インスタンス固定にすると層を区別できない**（IADR-0212 §課題）。
// 既定 null＝呼び出し側が用途を名乗らない場合で、実装側が安全既定（取引判断）へ倒す。
public interface ILlmCompletionClient
{
    Task<string> CompleteAsync(
        string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default);
}
