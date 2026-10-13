using System.Globalization;

namespace TradeDecisionService.Domain;

/// <summary>
/// FR-04, FR-11, #1332, IADR-0531: 解析不能だった LLM 応答の**形の指紋**（<see cref="TradeDecisionParser.DescribeShape"/> が作る）。
/// 本文を読まずに <see cref="TradeDecisionParseFailureKind.NoJsonObject"/> の原因の候補を分ける。数は ASCII の文字を数える
/// （<see cref="FullWidthBraces"/> だけは全角の <c>｛</c> <c>｝</c>）。
/// </summary>
/// <param name="Length">本文の文字数（UTF-16）。</param>
/// <param name="OpenBraces">ASCII の <c>{</c> の数。</param>
/// <param name="CloseBraces">ASCII の <c>}</c> の数。</param>
/// <param name="FullWidthBraces">全角の <c>｛</c> と <c>｝</c> の数の和。</param>
/// <param name="Quotes">ASCII の <c>"</c> の数（エスケープを含む）。</param>
/// <param name="CodeFences"><c>```</c> の数。</param>
/// <param name="Newlines">LF の数（抜粋はログの 1 行化で改行を <c>_</c> に置き換えるため、元の改行数をここに残す）。</param>
/// <param name="FirstBraceIndex">最初の <c>{</c> の位置（無ければ -1）。</param>
/// <param name="ObjectClosed">最初の <c>{</c> からの走査が深さ 0 に戻ったか（＝抽出できたか）。</param>
/// <param name="EndedInString">走査が末尾で文字列の内側にいたか（閉じない <c>"</c>）。</param>
/// <param name="DepthAtEnd">走査が末尾で残した深さ（閉じなかった <c>{</c> の数）。</param>
public sealed record UnparseableOutputShape(
    int Length, int OpenBraces, int CloseBraces, int FullWidthBraces, int Quotes, int CodeFences, int Newlines,
    int FirstBraceIndex, bool ObjectClosed, bool EndedInString, int DepthAtEnd)
{
    /// <summary>
    /// #1332, IADR-0531 決定 2: 原因の候補。<see cref="UnparseableOutputSuspect.NotApplicable"/> は抽出できた
    /// （＝<c>NoJsonObject</c> ではない）こと。判定の順は「括弧が無い → 全角 → 文字列が閉じない → 括弧が閉じない」。
    /// </summary>
    public UnparseableOutputSuspect Suspect =>
        ObjectClosed ? UnparseableOutputSuspect.NotApplicable
        : FirstBraceIndex < 0 && FullWidthBraces > 0 ? UnparseableOutputSuspect.FullWidthBraces
        : FirstBraceIndex < 0 ? UnparseableOutputSuspect.NoBrace
        : EndedInString ? UnparseableOutputSuspect.UnterminatedString
        : UnparseableOutputSuspect.UnclosedBrace;

    /// <summary>ログの 1 値として出す要約（<c>キー=値</c> を空白で区切る。順序は固定）。</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"suspect={Suspect} length={Length} braces={OpenBraces}/{CloseBraces} fullWidthBraces={FullWidthBraces} "
        + $"quotes={Quotes} fences={CodeFences} newlines={Newlines} firstBrace={FirstBraceIndex} "
        + $"endedInString={EndedInString} depthAtEnd={DepthAtEnd}");
}

/// <summary>#1332, IADR-0531: <c>NoJsonObject</c> の原因の候補（形の指紋から機械的に決まる）。</summary>
public enum UnparseableOutputSuspect
{
    /// <summary>抽出はできた（<c>MalformedJson</c> / <c>UnknownAction</c> / <c>InvalidValues</c> の原本）。</summary>
    NotApplicable,

    /// <summary>
    /// ASCII の <c>{</c> が無い。散文だけ（拒否・説明・「以下が出力です」で終わる）か、JSON が 2 個目の text ブロックにあり
    /// 基盤のゲートウェイが 1 個目しか返さなかった。両者は抜粋の末尾で見分ける。
    /// </summary>
    NoBrace,

    /// <summary>ASCII の <c>{</c> が無く、全角の括弧がある（全角で書いた JSON）。</summary>
    FullWidthBraces,

    /// <summary>
    /// 最初の <c>{</c> からの走査が末尾で文字列の内側にいる（エスケープされない <c>"</c> が奇数個）。語の化け（#1290）が
    /// <c>"</c> を含むトークンを出した、根拠文に <c>"</c> を素で書いた、など。
    /// </summary>
    UnterminatedString,

    /// <summary>
    /// 最初の <c>{</c> からの走査が深さを残した。JSON より前の散文に閉じない <c>{</c> がある、または JSON の閉じ <c>}</c> が無い。
    /// </summary>
    UnclosedBrace,
}

/// <summary>
/// FR-04, FR-11, #1332, IADR-0531 決定 1: 解析不能の原本の**抜粋**。先頭と末尾を残して中央を省く（前置きの散文も
/// 末尾の閉じ括弧も見える）。量の上限は <see cref="HeadChars"/> ＋ <see cref="TailChars"/> 字＋省略の注記。
/// ログの 1 行化（<c>LogSanitizer.Sanitize</c>）は呼び出し側が 1 回だけ掛ける（多段に通さない）。
/// </summary>
public static class UnparseableOutputExcerpt
{
    /// <summary>残す先頭の文字数。</summary>
    public const int HeadChars = 1_000;

    /// <summary>残す末尾の文字数。</summary>
    public const int TailChars = 500;

    /// <summary>先頭 <see cref="HeadChars"/> 字と末尾 <see cref="TailChars"/> 字を残す。短ければ全文。サロゲートペアは割らない。</summary>
    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= HeadChars + TailChars)
        {
            return text ?? string.Empty;
        }

        var headEnd = HeadChars;
        if (char.IsHighSurrogate(text[headEnd - 1]))
        {
            headEnd--;
        }

        var tailStart = text.Length - TailChars;
        if (char.IsLowSurrogate(text[tailStart]))
        {
            tailStart++;
        }

        var omitted = tailStart - headEnd;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{text[..headEnd]}…(中略 {omitted} 文字)…{text[tailStart..]}");
    }
}
