using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReportService.Domain;

// FR-07, FR-14, UC-03, ADR-0003, #1016, IADR-0431 決定 2: 利用者の自由文の指示から LLM が作った**方針の改訂案**。
// 確定するまで取引には効かない（ADR-0003・FR-07）。監視銘柄の入れ替え案は**提示と記録だけ**であり、
// 適用は設定画面（SC-02）で行う（FR-13・FR-14「設定値の変更は Discord からは参照のみ」）。
public sealed record PolicyRevisionProposal(
    string PolicySummary,
    IReadOnlyList<WatchlistChangeSuggestion> WatchlistChanges,
    string? Rationale);

// 監視銘柄の入れ替え案の 1 件。Symbol は米国のティッカー書式に限る（検証済み）。
public sealed record WatchlistChangeSuggestion(WatchlistChangeAction Action, string Symbol, string Reason);

public enum WatchlistChangeAction
{
    Add,
    Remove,
}

// FR-13, ADR-0042 決定 1, #1025, IADR-0433 決定 1: 案を作った時点の監視銘柄の 1 件（Market は市場の名前 "UnitedStates" / "Japan"）。
// 列挙の JSON 表現（数値／名前）にサービス間で結合しないため、名前の文字列で運ぶ。
public sealed record WatchlistSnapshotItem(string Symbol, string Market)
{
    public const string UnitedStates = "UnitedStates";
    public const string Japan = "Japan";
    public const int MaxCount = 200;

    // 銘柄コードの値域（台帳・監視銘柄の表記。英数字・ピリオド・ハイフン 1〜16 文字。Bot の BotCommandParser.IsSymbol と同じ形）。
    public static bool IsValid(string? symbol, string? market) =>
        !string.IsNullOrEmpty(symbol) && symbol.Length <= 16
        && symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
        && market is UnitedStates or Japan;
}

// 案の検証結果。Proposal が null なら Reason に**なぜ捨てたか**が入る（利用者へそのまま見せる定数文）。
public sealed record PolicyRevisionParseResult(PolicyRevisionProposal? Proposal, string? Reason)
{
    public bool IsValid => Proposal is not null;

    public static PolicyRevisionParseResult Valid(PolicyRevisionProposal proposal) => new(proposal, null);

    public static PolicyRevisionParseResult Invalid(string reason) => new(null, reason);
}

// FR-07, ADR-0003, #1016, IADR-0431 決定 2: LLM の出力を厳格なスキーマで検証する（純関数・決定的）。
//
// 🔴 **1 つでも外れたら案全体を捨てる（部分採用しない）。** 形式に従わなかった出力の一部だけを「正しそうだから」と
// 拾うと、指示の取り違え（例: 追加と除外の取り違え・銘柄の打ち間違い）が案に紛れて利用者のレビューを素通りしやすい。
// 捨てた場合は「案なし」として理由を返し、何も保存しない（原則 A: 案が無いことを「空の案」に見せない）。
//
// 未知の項目は読まない（使わない）。LLM が勝手に足した項目で、スキーマ外の操作（注文・設定変更等）が表現されても
// それを実行する経路は存在しない——扱うのは下の 3 項目だけである。
public static class PolicyRevisionProposalParser
{
    public const int MaxPolicySummaryLength = 2000;
    public const int MaxRationaleLength = 1000;
    public const int MaxReasonLength = 200;
    public const int MaxAdditions = 5;
    public const int MaxRemovals = 5;

    // 米国のティッカー書式（例 AAPL / BRK.B / BF-B）。大文字 1〜5 文字＋任意の区切り（. または -）と 1〜2 文字。
    // アンカーは `\A…\z`（.NET の `$` は末尾 LF の直前にもマッチする。#837）。
    private static readonly Regex UsTickerPattern =
        new(@"\A[A-Z]{1,5}([.-][A-Z]{1,2})?\z", RegexOptions.CultureInvariant);

    public static bool IsUsTicker(string? value) => value is not null && UsTickerPattern.IsMatch(value);

    // 収集情報の境界語（IADR-0022 / ReportSummarySanitizer と同じ値）。**含む出力は案全体を捨てる**——表示の無害化で
    // 取り除くと、表示と確定される原文が食い違う（IADR-0431 決定 5・ADR-0003）。受け入れて素通しもしない（境界の偽装）。
    private static readonly string[] BoundaryMarkers = ["<<<UNTRUSTED_DATA", "UNTRUSTED_DATA>>>"];

    private static bool HasBoundaryMarker(string text) =>
        BoundaryMarkers.Any(m => text.Contains(m, StringComparison.Ordinal));

    public static PolicyRevisionParseResult Parse(string? llmText)
    {
        if (string.IsNullOrWhiteSpace(llmText))
            return PolicyRevisionParseResult.Invalid("AI の出力が空でした");

        // フェンス（```json … ```）や前置きの文があっても、最初の `{` から最後の `}` までを JSON として読む。
        var start = llmText.IndexOf('{', StringComparison.Ordinal);
        var end = llmText.LastIndexOf('}');
        if (start < 0 || end <= start)
            return PolicyRevisionParseResult.Invalid("AI の出力が JSON ではありませんでした");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(llmText[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return PolicyRevisionParseResult.Invalid("AI の出力が JSON として解釈できませんでした");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PolicyRevisionParseResult.Invalid("AI の出力が JSON オブジェクトではありませんでした");

            // 方針（必須）。
            if (!root.TryGetProperty("policySummary", out var policyElement) || policyElement.ValueKind != JsonValueKind.String)
                return PolicyRevisionParseResult.Invalid("AI の出力に方針（policySummary）がありませんでした");

            var policy = CleanText(policyElement.GetString());
            if (policy.Length == 0)
                return PolicyRevisionParseResult.Invalid("AI の出力の方針（policySummary）が空でした");
            if (policy.Length > MaxPolicySummaryLength)
                return PolicyRevisionParseResult.Invalid($"AI の出力の方針が長すぎます（{MaxPolicySummaryLength} 文字まで）");
            if (HasBoundaryMarker(policy))
                return PolicyRevisionParseResult.Invalid("AI の出力の方針に収集情報の境界語が含まれていました");

            // 監視銘柄の入れ替え案（任意。無い・null は「入れ替えなし」＝空配列。**形式違反は捨てる**）。
            var changes = new List<WatchlistChangeSuggestion>();
            if (root.TryGetProperty("watchlistChanges", out var changesElement)
                && changesElement.ValueKind != JsonValueKind.Null)
            {
                if (changesElement.ValueKind != JsonValueKind.Array)
                    return PolicyRevisionParseResult.Invalid("AI の出力の監視銘柄の入れ替え案（watchlistChanges）が配列ではありませんでした");

                foreach (var item in changesElement.EnumerateArray())
                {
                    if (ParseChange(item) is not { } change)
                        return PolicyRevisionParseResult.Invalid(
                            "AI の出力の監視銘柄の入れ替え案に形式違反がありました（操作は add / remove、銘柄は米国のティッカー、理由は必須・"
                            + $"{MaxReasonLength} 文字まで）");
                    changes.Add(change);
                }
            }

            if (changes.Count(c => c.Action == WatchlistChangeAction.Add) > MaxAdditions)
                return PolicyRevisionParseResult.Invalid($"AI の出力の監視銘柄の追加案が多すぎます（{MaxAdditions} 件まで）");
            if (changes.Count(c => c.Action == WatchlistChangeAction.Remove) > MaxRemovals)
                return PolicyRevisionParseResult.Invalid($"AI の出力の監視銘柄の除外案が多すぎます（{MaxRemovals} 件まで）");
            if (changes.Select(c => c.Symbol).Distinct(StringComparer.Ordinal).Count() != changes.Count)
                return PolicyRevisionParseResult.Invalid("AI の出力の監視銘柄の入れ替え案に同じ銘柄が重複していました");

            // 説明（任意）。
            string? rationale = null;
            if (root.TryGetProperty("rationale", out var rationaleElement) && rationaleElement.ValueKind != JsonValueKind.Null)
            {
                if (rationaleElement.ValueKind != JsonValueKind.String)
                    return PolicyRevisionParseResult.Invalid("AI の出力の説明（rationale）が文字列ではありませんでした");

                var cleaned = CleanText(rationaleElement.GetString());
                if (cleaned.Length > MaxRationaleLength)
                    return PolicyRevisionParseResult.Invalid($"AI の出力の説明が長すぎます（{MaxRationaleLength} 文字まで）");
                if (HasBoundaryMarker(cleaned))
                    return PolicyRevisionParseResult.Invalid("AI の出力の説明に収集情報の境界語が含まれていました");
                rationale = cleaned.Length == 0 ? null : cleaned;
            }

            return PolicyRevisionParseResult.Valid(new PolicyRevisionProposal(policy, changes, rationale));
        }
    }

    private static WatchlistChangeSuggestion? ParseChange(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        if (!item.TryGetProperty("action", out var actionElement) || actionElement.ValueKind != JsonValueKind.String)
            return null;

        WatchlistChangeAction? action = actionElement.GetString() switch
        {
            "add" => WatchlistChangeAction.Add,
            "remove" => WatchlistChangeAction.Remove,
            _ => null,
        };
        if (action is null)
            return null;

        // 銘柄は大小文字を補正しない（打ち間違いを推測で直さない。書式外は捨てる）。
        if (!item.TryGetProperty("symbol", out var symbolElement) || symbolElement.ValueKind != JsonValueKind.String)
            return null;
        var symbol = symbolElement.GetString();
        if (!IsUsTicker(symbol))
            return null;

        if (!item.TryGetProperty("reason", out var reasonElement) || reasonElement.ValueKind != JsonValueKind.String)
            return null;
        var reason = CleanText(reasonElement.GetString());
        if (reason.Length == 0 || reason.Length > MaxReasonLength || HasBoundaryMarker(reason))
            return null;

        return new WatchlistChangeSuggestion(action.Value, symbol!, reason);
    }

    // 制御文字を落とす（改行だけ残す）。CR は落ちるため CRLF は LF になる。前後の空白を落とす。
    // 🔴 **正規化はここ（検証時）だけで行う。** 保存する方針はこの結果であり、表示はこれに幅ゼロ空白を挿すだけ
    // （空行の畳み込み等をしない）——表示と確定される原文を食い違わせない（IADR-0431 決定 5）。
    internal static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch != '\n')
                continue;
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }
}
