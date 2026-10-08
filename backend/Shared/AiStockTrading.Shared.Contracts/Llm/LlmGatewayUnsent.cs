using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Logging;

namespace AiStockTrading.Shared.Contracts.Llm;

// FR-04, FR-09, FR-11, #1267, IADR-0517: LLM ゲートウェイの `Sent=false`（送信しなかった応答）の原因を
// **ゲートウェイが返したまま**読むための語彙と要約。
//
// 🔴 **`Sent=false` を「機密区分による縮退」と断定しない。** ゲートウェイは越境の拒否・プロバイダ未登録・
// 上流の不調（429・401・5xx）をどれも同じ `Sent=false` で返す（MSP `CompletionUseCase`）。2026-10-07 の PoC で
// 判断 132 件が「機密区分による縮退」という誤った理由だけを残して Hold に固定され、原因を追えなかった（#1267）。
// 呼び出し元 3 か所（取引判断・報告書の散文・方針の改訂）が同じ要約を使い、片方だけ直る事故を入れない。
//
// 原因の種類（`failureKind`: 文字列 "egress_denied" / "provider_missing" / "upstream_error"）と上流の状態コード
// （`upstreamStatusCode`: upstream_error で上流が HTTP ステータスを返したときだけ）は MSP#1819（MSP#1824）が足す任意の
// フィールドである。読み取りは寛容にする: 欠落（旧い基盤）・未知の値・想定外の型はすべて null（原因不明）へ落とし、
// 例外にしない（MSP の PR のマージ順に依存しない）。
public enum LlmGatewayUnsentKind
{
    /// <summary>越境の拒否（機密区分・用途で送信先が許されない）。</summary>
    EgressDenied,

    /// <summary>送信先のプロバイダが未登録（ゲートウェイの構成の欠落）。</summary>
    ProviderMissing,

    /// <summary>上流（LLM 提供側）の不調（429・401・5xx・通信断）。</summary>
    UpstreamError,
}

/// <summary><c>Sent=false</c> 1 件の原因（ゲートウェイの申告を正規化した値）。</summary>
/// <param name="Kind">原因の種類。未報告・未知は <c>null</c>。</param>
/// <param name="UpstreamStatusCode">上流の HTTP 状態コード。未報告は <c>null</c>。</param>
/// <param name="RoutingReason">ゲートウェイの判定理由（正規化・切り詰め・秘密の伏せ字済み）。未報告は <c>null</c>。</param>
/// <param name="GatewayText">ゲートウェイが本文（Text）に載せた説明の要約（同上）。未報告は <c>null</c>。</param>
public sealed record LlmGatewayUnsentCause(
    LlmGatewayUnsentKind? Kind, int? UpstreamStatusCode, string? RoutingReason, string? GatewayText)
{
    /// <summary>種類の表示名（日本語）。未報告は「種別不明」。</summary>
    public string KindLabel => LlmGatewayUnsent.Label(Kind);

    /// <summary>
    /// 1 行の説明（ログ・判断理由・通知で共通）。🔴 種別が不明なときに原因を推測で埋めない。
    /// </summary>
    public string Describe()
    {
        var parts = new List<string> { $"種別: {KindLabel}" };
        if (UpstreamStatusCode is { } status)
            parts.Add($"上流 {status.ToString(CultureInfo.InvariantCulture)}");
        var reason = TrimEnd(RoutingReason);
        var text = TrimEnd(GatewayText);
        parts.Add($"理由: {reason ?? "（ゲートウェイの申告なし）"}");
        // 越境の拒否は本文にも同じ理由を載せる（MSP `CompletionUseCase`）。同じ文を 2 度書かない。
        if (text is not null && !string.Equals(text, reason, StringComparison.Ordinal))
            parts.Add($"ゲートウェイ: {text}");
        return string.Join("／", parts);
    }

    // 文の句点で終わる申告を、続けて句点を置く文面へ埋めても「。。」にしない。
    private static string? TrimEnd(string? value) =>
        value?.TrimEnd('。', '.', ' ') is { Length: > 0 } trimmed ? trimmed : null;
}

public static class LlmGatewayUnsent
{
    /// <summary>理由・本文の要約の長さ上限（文字数）。判断理由・通知へ載せるため短く保つ。</summary>
    public const int SummaryMaxLength = 160;

    /// <summary>応答からの原因の組み立て。<c>Sent=true</c> でも呼べる（値は読むだけ）。</summary>
    public static LlmGatewayUnsentCause From(LlmCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new LlmGatewayUnsentCause(
            payload.FailureKind,
            payload.UpstreamStatusCode,
            Summarize(payload.RoutingReason),
            Summarize(payload.Text));
    }

    /// <summary>
    /// 連続の用途別の件数の表示（例: <c>内訳 trade-decision 1・trade-decision-screening 4</c>）。無ければ空文字。
    /// 通知と台帳の要約で共通に使う（#1267 の AI レビュー 🟡）。
    /// </summary>
    public static string FormatBreakdown(IReadOnlyDictionary<string, int>? byPurpose) =>
        byPurpose is null || byPurpose.Count == 0
            ? string.Empty
            : "内訳 " + string.Join("・", byPurpose
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key} {p.Value.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>種類の表示名。</summary>
    public static string Label(LlmGatewayUnsentKind? kind) => kind switch
    {
        LlmGatewayUnsentKind.EgressDenied => "越境の拒否",
        LlmGatewayUnsentKind.ProviderMissing => "プロバイダ未登録",
        LlmGatewayUnsentKind.UpstreamError => "上流の不調",
        _ => "種別不明",
    };

    /// <summary>
    /// 文字列の原因の種類を寛容に読む。大文字小文字・区切り（<c>_</c> / <c>-</c>）を無視し、
    /// 未知の値・数字だけの値（序数は双方で揃っている保証が無い）は <c>null</c>。
    /// </summary>
    public static LlmGatewayUnsentKind? ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0 || normalized.All(char.IsAsciiDigit))
            return null;

        foreach (var kind in Enum.GetValues<LlmGatewayUnsentKind>())
        {
            if (string.Equals(kind.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                return kind;
        }

        return null;
    }

    /// <summary>JSON 値の原因の種類。文字列だけを読み、数値・オブジェクト・null は <c>null</c>（例外にしない）。</summary>
    public static LlmGatewayUnsentKind? ParseKind(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element ? ParseKind(element.GetString()) : null;

    /// <summary>JSON 値の状態コード。100〜599 の整数（数値または数字の文字列）だけを読み、他は <c>null</c>。</summary>
    public static int? ParseStatusCode(JsonElement? value)
    {
        if (value is not { } element)
            return null;

        int code;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt32(out code):
                break;
            case JsonValueKind.String when int.TryParse(
                element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out code):
                break;
            default:
                return null;
        }

        return code is >= 100 and <= 599 ? code : null;
    }

    /// <summary>
    /// ゲートウェイの文字列を 1 行・短く・秘密を伏せた形にする。空白だけなら <c>null</c>。
    /// <para>
    /// 🔴 **秘密を含まない範囲で**（#1267）: 上流の例外文に資格情報が混ざる経路を想定し、
    /// Bearer トークン・<c>sk-</c> 形式の鍵・<c>key=値</c> 形式・32 文字以上の連続した英数字列を伏せる。
    /// 伏せた後に <see cref="LogSanitizer"/> で行区切りを潰して切り詰める（多段に通さないため、ここで 1 回だけ）。
    /// </para>
    /// </summary>
    public static string? Summarize(string? value, int maxLength = SummaryMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // 正規表現へ渡す長さを先に抑える（応答本文は上限が無い。切り詰め後の要約は SummaryMaxLength 文字）。
        var input = value.Trim();
        if (input.Length > RedactionInputMaxLength)
            input = input[..RedactionInputMaxLength];

        var redacted = BearerPattern.Replace(input, "$1***");
        redacted = SecretKeyPattern.Replace(redacted, "***");
        redacted = KeyValuePattern.Replace(redacted, "$1=***");
        redacted = LongTokenPattern.Replace(redacted, "***");
        return LogSanitizer.Sanitize(redacted, maxLength);
    }

    private const int RedactionInputMaxLength = 2000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex BearerPattern = new(
        @"(?i)(bearer\s+)[A-Za-z0-9._~+/=\-]+", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex SecretKeyPattern = new(
        @"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{6,}", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex KeyValuePattern = new(
        @"(?i)\b(api[_\-]?key|access[_\-]?token|token|secret|password|authorization)\s*[:=]\s*[^\s,;)」）]+",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex LongTokenPattern = new(
        @"[A-Za-z0-9_\-+/=]{32,}", RegexOptions.CultureInvariant, RegexTimeout);
}
