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
// NFR（費用）, #347, IADR-0212/0218: Purpose は**その呼び出しの用途**であり、必須（既定値を置かない）。
// 二段判断は層ごとに用途が分かれる（一次=trade-decision-screening／二次=trade-decision）ため、
// 計上側の purpose を**アダプタの構築時に固定すると層が混ざる**。省略可にすると載せ忘れが静かに通るので、
// 位置引数の先頭に置いて**書かなければコンパイルが通らない**形にする（報告書側 ILlmUsageReporter と同型）。
public readonly record struct LlmUsage(string Purpose, int InputTokens, int OutputTokens, string? Model = null);
