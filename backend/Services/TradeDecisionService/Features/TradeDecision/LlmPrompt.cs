namespace TradeDecisionService.Features.TradeDecision;

// FR-04, NFR（費用）, #1523, IADR-0607 決定2: ゲートウェイへ送るプロンプトを「キャッシュできる接頭辞」と「残り」に分けた形。
// モデルが読む全文は CacheablePrefix + Rest（区切りを足さない。MSP の IADR-0671）。CacheablePrefix の null は「分けない」。
// 🔴 接頭辞に呼び出しごとに変わる値（時刻・現在値・保有・銘柄）を入れない。1 バイトでも違うとキャッシュは読まれず、書きの割増だけが掛かる。
public sealed record LlmPrompt(string? CacheablePrefix, string Rest)
{
    /// <summary>モデルが読む全文（従前のプロンプトと同じ文字列）。</summary>
    public string FullText => CacheablePrefix is null ? Rest : CacheablePrefix + Rest;

    /// <summary>
    /// 全文を接頭辞と残りに分ける。<paramref name="prefix"/> が null・空、全文の先頭と一致しない（序数比較）、
    /// または全文と同じ長さ（残りが空）なら分けない（接頭辞 null・残り＝全文）。分けても分けなくても <see cref="FullText"/> は全文のまま。
    /// </summary>
    public static LlmPrompt Split(string fullText, string? prefix)
    {
        ArgumentNullException.ThrowIfNull(fullText);
        return !string.IsNullOrEmpty(prefix)
               && fullText.Length > prefix.Length
               && fullText.StartsWith(prefix, StringComparison.Ordinal)
            ? new LlmPrompt(prefix, fullText[prefix.Length..])
            : new LlmPrompt(null, fullText);
    }
}
