using System.Text;

namespace AiStockTrading.InformationCollection.Domain;

// FR-01, ADR-0003, アーキ概要「ニュース入力の防御」: 取得テキストを LLM 文脈で「命令ではなくデータ」として扱うための
// 構造的分離（spotlighting）。本文を境界デリミタでラップし、本文中のデリミタ衝突を除去、制御文字を除去する。
// これは多層防御の一層であり、注入を完全排除するものではない（IADR-0022）。
public static class PromptSafetySanitizer
{
    // データ境界（本文がこのブロックを抜け出せないよう、本文中の同一断片は除去する）。
    public const string OpenDelimiter = "<<<UNTRUSTED_DATA";
    public const string CloseDelimiter = "UNTRUSTED_DATA>>>";

    public static string Sanitize(string? rawText)
    {
        var text = rawText ?? string.Empty;

        // 境界デリミタの衝突を防ぐ（データがブロックを抜け出す/偽の境界を作るのを防止）。
        text = text
            .Replace(OpenDelimiter, string.Empty, StringComparison.Ordinal)
            .Replace(CloseDelimiter, string.Empty, StringComparison.Ordinal);

        // 制御文字（改行・タブ以外）を除去する。
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not ('\n' or '\t'))
                continue;
            sb.Append(ch);
        }

        return $"{OpenDelimiter}\n{sb.ToString().Trim()}\n{CloseDelimiter}";
    }

    // 既定の境界でラップ済みかを判定する（冪等性・テスト用）。
    public static bool IsWrapped(string? text) =>
        text is not null
        && text.StartsWith(OpenDelimiter, StringComparison.Ordinal)
        && text.EndsWith(CloseDelimiter, StringComparison.Ordinal);
}
