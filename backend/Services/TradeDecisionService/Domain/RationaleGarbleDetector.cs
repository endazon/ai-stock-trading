namespace TradeDecisionService.Domain;

// 🔴 FR-04, FR-11, #1290, IADR-0524 決定 2（利用者裁定 2026-10-10）: LLM の根拠文（rationale）の文字化けの疑いを検出する純関数。
//
// 実測（PoC 2026-10-06〜09・一次スクリーニング）: 「監視銘柄」の「柄」の位置が、別の漢字（監視銘牌・監視銘姓）・置換文字（U+FFFD）・
// 無関係な英字列やキリル文字列（監視銘HeaderItem・監視銘româ内・監視銘събњект）へ化けた。モデル側のサンプリングで起き、
// action は壊れていない。🔴 **検出しても action は変えない**（Hold に倒さない）。検出は警告ログと転記時の目印のためだけにある。
//
// 規則（モデルに依存しない。誤検出を避けて保守側に倒す）:
//   1. 置換文字 U+FFFD を 1 つでも含めば疑う。
//   2. キリル文字の並びが、漢字・かなに直接（空白なしで）接していれば疑う（日本語の根拠文にキリル文字の正当な用途は無い）。
//   3. ラテン文字（ASCII・ラテン 1 補助・拡張 A/B の文字）の並び（英字・数字・「.」「_」「-」「/」「:」の連なり）が、
//      直前に漢字・直後に漢字かかなを、どちらも空白なしで持ち、かつ次のいずれでもなければ疑う:
//        - 小文字を含まない（AAPL・RSI・S1・BRK.B・Q3 等の銘柄・略語）
//        - 英字が 2 文字未満（「eコマース」の e 等）
//        - URL（「://」か「www.」を含む）
//        - 既知の語（出力形式の Buy/Sell/Hold・null/true/false・項目名。大小文字を問わない）
//
// 🔴 限界（検出できない・誤検出し得るもの）:
//   - 別の**有効な漢字**への化け（監視銘牌・監視銘姓）は検出できない（辞書が要る）。固定文からの語の言い換え（IADR-0524 決定 1）で出現を減らす。
//   - 化けた英字列が文末・句読点・空白の前にあると検出しない（両側を要求するため。例「監視銘gl。」）。
//   - 漢字に挟まれた英語の固有名（「米国Apple社」）は誤検出する。目印が付くだけで action は変わらない。
public static class RationaleGarbleDetector
{
    /// <summary>根拠文の転記に付ける目印。LLM の文言と区別できるように記号で始める。</summary>
    public const string Marker = "⚠ 判断理由に文字化けの疑い";

    private const char ReplacementCharacter = '\uFFFD';

    // 規則 3 の除外（大小文字を問わない完全一致）。出力形式（TradeDecisionParser の項目と action の値）に限る。
    private static readonly HashSet<string> KnownTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Buy", "Sell", "Hold", "null", "true", "false",
        "action", "rationale", "referencePrice", "stopLossDistancePerShare", "expectedProfitPerShare",
    };

    /// <summary>根拠文に文字化けの疑いがあるか（規則は型の注記）。null・空は疑わない。</summary>
    public static bool IsSuspected(string? rationale)
    {
        if (string.IsNullOrEmpty(rationale))
            return false;

        if (rationale.Contains(ReplacementCharacter, StringComparison.Ordinal))
            return true;

        var i = 0;
        while (i < rationale.Length)
        {
            var c = rationale[i];
            if (IsCyrillic(c))
            {
                var end = i;
                while (end < rationale.Length && IsCyrillic(rationale[end]))
                    end++;
                if ((i > 0 && IsCjkOrKana(rationale[i - 1])) || (end < rationale.Length && IsCjkOrKana(rationale[end])))
                    return true;
                i = end;
                continue;
            }

            if (IsLatinLetter(c) || IsAsciiDigit(c))
            {
                var end = i;
                while (end < rationale.Length && IsLatinRunChar(rationale[end]))
                    end++;
                if (i > 0 && IsCjkIdeograph(rationale[i - 1])
                    && end < rationale.Length && IsCjkOrKana(rationale[end])
                    && IsSuspiciousLatinRun(rationale.AsSpan(i, end - i)))
                {
                    return true;
                }

                i = end;
                continue;
            }

            i++;
        }

        return false;
    }

    /// <summary>転記用に目印を前置した根拠文（原文は書き換えない）。<paramref name="suspected"/> が false なら原文のまま。</summary>
    public static string Mark(string rationale, bool suspected)
    {
        ArgumentNullException.ThrowIfNull(rationale);
        if (!suspected || rationale.StartsWith(Marker, StringComparison.Ordinal))
            return rationale;
        return $"{Marker}: {rationale}";
    }

    private static bool IsSuspiciousLatinRun(ReadOnlySpan<char> run)
    {
        var letters = 0;
        var hasLower = false;
        foreach (var c in run)
        {
            if (!IsLatinLetter(c))
                continue;
            letters++;
            if (char.IsLower(c))
                hasLower = true;
        }

        if (letters < 2 || !hasLower)
            return false;

        if (run.Contains("://", StringComparison.Ordinal) || run.Contains("www.", StringComparison.OrdinalIgnoreCase))
            return false;

        return !KnownTerms.Contains(run.ToString());
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    // ラテン文字: ASCII の英字、ラテン 1 補助の文字（× ÷ を除く）、ラテン拡張 A・B。全角英数字（U+FF21 等）は含めない。
    private static bool IsLatinLetter(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
        || (c is >= '\u00C0' and <= '\u024F' && c != '\u00D7' && c != '\u00F7');

    private static bool IsLatinRunChar(char c) =>
        IsLatinLetter(c) || IsAsciiDigit(c) || c is '.' or '_' or '-' or '/' or ':';

    private static bool IsCyrillic(char c) => c is >= '\u0400' and <= '\u052F';

    // 漢字: CJK 統合漢字・拡張 A・互換漢字と「々」。
    private static bool IsCjkIdeograph(char c) =>
        c is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF') or (>= '\uF900' and <= '\uFAFF') or '\u3005';

    // かな: ひらがな・カタカナ（長音符を含む）・半角カタカナ。
    private static bool IsKana(char c) =>
        c is (>= '\u3041' and <= '\u309F') or (>= '\u30A0' and <= '\u30FF') or (>= '\uFF66' and <= '\uFF9F');

    private static bool IsCjkOrKana(char c) => IsCjkIdeograph(c) || IsKana(c);
}
