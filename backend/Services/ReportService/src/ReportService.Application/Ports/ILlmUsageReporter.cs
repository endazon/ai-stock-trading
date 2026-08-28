namespace AiStockTrading.Report.Application.Ports;

// NFR（費用）, FR-06, FR-16, 05_trading-assumptions §6.1, #347, IADR-0219:
// 報告書生成の LLM 呼び出しのトークン使用量を費用計測へ渡すポート（計測点は egress）。
//
// 🔴 **報告書生成の費用は月次 LLM 費用上限の対象外だが、計上しないという意味ではない。**
// 計画 §6.1 は対象外の費用について「抑制動作も行わず、**月報に実績を記載する**」と定める。
// #282 で実測された「報告書散文費用の計上漏れ→過少申告」は、まさに計測点が無いことで起きた。
// 上限へ積むか否かは購読側（費用統制サービス）が用途（purpose）で判別するため、
// 本ポートの実装は**用途を必ず載せて**発行するだけでよい。
//
// 既定は NoOpLlmUsageReporter（publish しない＝fail-safe）。Worker が発行実装を配線する
// （取引判断サービスの同名ポートと同型。サービス間の直接参照は禁止のため各サービスが自前のポートを持つ）。
public interface ILlmUsageReporter
{
    Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default);
}

// 報告書生成 1 回分のトークン使用量。
// Purpose は用途キー（report-monthly / report-weekly / report-daily）で、費用の対象範囲判別に用いる。
// Model は**ゲートウェイが実際に選択したモデル**（応答の報告値）。単価解決の唯一の根拠であり（IADR-0122 決定1）、
// 月報の「当月の LLM 利用実績」にも用いる。既定 null＝モデル不明（計上側は過小計上を避ける側へ倒す）。
public readonly record struct LlmUsage(string Purpose, int InputTokens, int OutputTokens, string? Model = null);
