using System.Text;

namespace AiStockTrading.Shared.Contracts.Llm;

// FR-01, FR-08, ADR-0003, IADR-0022, IADR-0542, #1359: 外部由来の自由文（ニュースの表題・本文、KB から取り出した文書など）を
// LLM のプロンプトへ入れるときの**データ境界**（spotlighting）。純関数・共有部品。
//
// 🔴 **境界のラップはプロンプトを組む側だけが掛ける（IADR-0542 決定 1）。** 保存（KB）には掛けない。
//   保存に掛けると境界語が MSP の画面・Wiki・検索の入力まで漏れ、表題も約 37 文字長くなる（#1359 の実測。MSP#1966）。
//   保存側は <see cref="Neutralize"/>（境界語の衝突と制御文字の除去だけ）を掛け、素の文字列を保存する。
//
// 🔴 **境界語の除去は不変点まで繰り返す（IADR-0542 決定 3）。** 1 回の Replace では、入れ子
//   （`<<<UNTRUSTED_<<<UNTRUSTED_DATADATA`）から除去の結果として境界語が再生成され、データがブロックを抜け出せる。
//   本部品の出力（<see cref="StripMarkers"/>・<see cref="Neutralize"/> の戻り値、<see cref="Wrap"/> の中身）には
//   境界語が 1 つも残らないことを事後条件とする。
//
// 多層防御の一層であり、注入を完全排除するものではない（IADR-0022）。
public static class UntrustedDataBoundary
{
    /// <summary>データブロックの開始の境界語。</summary>
    public const string OpenMarker = "<<<UNTRUSTED_DATA";

    /// <summary>データブロックの終了の境界語。</summary>
    public const string CloseMarker = "UNTRUSTED_DATA>>>";

    /// <summary>
    /// 境界語（開始・終了）を、1 つも残らなくなるまで繰り返し取り除く。null は空文字。
    /// 既に永続化された境界語つきの文書（#1359 以前の保存分）を読むときにも使う（ラップの二重化を防ぐ）。
    /// </summary>
    public static string StripMarkers(string? text)
    {
        var current = text ?? string.Empty;
        while (true)
        {
            var next = current
                .Replace(OpenMarker, string.Empty, StringComparison.Ordinal)
                .Replace(CloseMarker, string.Empty, StringComparison.Ordinal);
            if (next.Length == current.Length)
                return next;

            current = next;
        }
    }

    /// <summary>
    /// 保存・ラップの前の無害化: 制御文字（改行・タブ以外）を除去し、境界語を不変点まで取り除き、前後の空白を落とす。
    /// 制御文字の除去を先に行う（制御文字を挟んだ境界語 `&lt;&lt;&lt;UNTRUSTED\u0001_DATA` が除去後に現れるのを防ぐ）。
    /// 境界語では囲わない（保存先・人間向けの表示へ境界語を漏らさない）。
    /// </summary>
    public static string Neutralize(string? text)
    {
        var raw = text ?? string.Empty;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) && ch is not ('\n' or '\t'))
                continue;
            sb.Append(ch);
        }

        return StripMarkers(sb.ToString()).Trim();
    }

    /// <summary>
    /// プロンプトへ入れる外部テキストを境界語で囲う（<see cref="Neutralize"/> を通してから囲う）。
    /// 入力が既に境界語つき（旧い保存分）でも、中の境界語を取り除いてから 1 組だけで囲う（冪等）。
    /// </summary>
    public static string Wrap(string? text) => $"{OpenMarker}\n{Neutralize(text)}\n{CloseMarker}";

    /// <summary>既定の境界で囲まれているか（冪等性・試験用）。</summary>
    public static bool IsWrapped(string? text) =>
        text is not null
        && text.StartsWith(OpenMarker, StringComparison.Ordinal)
        && text.EndsWith(CloseMarker, StringComparison.Ordinal);

    /// <summary>境界語（開始・終了のどちらか）を含むか（保存されるテキストに境界語が無いことの試験用）。</summary>
    public static bool ContainsMarker(string? text) =>
        text is not null
        && (text.Contains(OpenMarker, StringComparison.Ordinal) || text.Contains(CloseMarker, StringComparison.Ordinal));
}
