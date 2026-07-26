namespace AiStockTrading.Shared.Contracts.Llm;

// #247, FR-04, FR-06, FR-11, IADR-0104 決定1: platform LLM ゲートウェイ /complete 応答の終了理由（stopReason）の語彙。
// 上流（microservices-platform の Platform.Shared.Contracts.Dtos.CompletionStopReasons。MSP #379 / PR #391・MSP ADR-0025）
// の写像であり、本リポジトリからは上流アセンブリを参照できないため、判定を 1 箇所に置いて追随漏れの分散を防ぐ。
// 消費者は取引判断（HttpLlmCompletionClient）と報告書散文（HttpReportNarrativeDrafter）の 2 つ。
//
// enum にしないのは、語彙が増えたときに未知の値が既定値へ黙って落ちるのを避けるため（未知の理由はそのまま透過し、
// ログへ残す）。判定は大小を無視した完全一致のみで、部分一致・前方一致は行わない（誤検知で正常な判断まで
// 止めない）。
//
// 置き場所は Events 名前空間の外である（IADR-0079 のイベント後方互換契約テストは EventTypeDiscovery ＝
// Events 名前空間の record 型のみを母集合とするため、event-schemas.baseline には影響しない）。
public static class LlmStopReasons
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string Refusal = "refusal";
    public const string StopSequence = "stop_sequence";
    public const string ToolUse = "tool_use";

    // モデルが拒否した（安全性分類器による停止）。呼び出し側は本文を判断・成果物の根拠に使ってはならない。
    public static bool IsRefusal(string? stopReason) =>
        string.Equals(stopReason, Refusal, StringComparison.OrdinalIgnoreCase);

    // 出力上限に到達した（拒否ではなく劣化）。thinking が既定で有効なモデルでは本文が空または途中で切れる（IADR-0101）。
    public static bool IsMaxTokens(string? stopReason) =>
        string.Equals(stopReason, MaxTokens, StringComparison.OrdinalIgnoreCase);
}
