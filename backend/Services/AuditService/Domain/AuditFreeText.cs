using System.Text;
using System.Text.RegularExpressions;

namespace AuditService.Domain;

// FR-11, NFR（セキュリティ・NFR-10 の 7 年保持）, #842, IADR-0405: 監査台帳の**自由記述欄**へ入る文面を整える。
//
// 🔴 監査台帳（`audit_events`）の `Detail` は契約イベントの全量 JSON であり 7 年消せない。任意長の
// `Exception.Message` がそのまま入ると、長さの上限が無く、接続先（OpenD のホスト:ポート等）のような
// 業務の判断に要らない基盤の情報まで残る（#842 の実測: `OpenD への InitConnect が失敗しました（opend.ai-stock-trading.svc:11111）。`）。
//
// 線引き（IADR-0405 決定2）:
// - **載せてよい**: ブローカーの応答文（retMsg）・発注前検証の定型文・例外の種類と要旨。
// - **伏せる**: 接続先（URL・ホスト名:ポート・IPv4・[IPv6]:ポート）→ `［接続先］`。
// - **畳む**: 改行・タブ等の制御文字は空白 1 つ（台帳の 1 行要約を崩さない）。
// - **上限**: <see cref="MaxLength"/> 文字。超えたら切り詰めて `…` を付ける（サロゲートペアは割らない）。
//
// 秘匿名のプロパティの全数走査（AuditPayloadSecretExposureTests）は「どの欄があるか」を見る。本型は
// 「欄の中身が何を運び得るか」を見る側であり、両者は補い合う（置き換えない）。
public static class AuditFreeText
{
    /// <summary>自由記述欄 1 つの上限（文字数）。実測の最長級（送信後に結果を確認できない旨＋内側の retMsg）で約 250 文字。</summary>
    public const int MaxLength = 500;

    /// <summary>接続先を伏せた箇所に置く語。</summary>
    public const string EndpointPlaceholder = "［接続先］";

    // URL（scheme://…）／[IPv6]:port／IPv4（任意の :port）／英字を含むドット区切りのホスト名:port／localhost:port。
    // 時刻（10:30）・価格（329.03）・`retType=-1` を拾わないよう、ホスト名は「ドットを含み英字を含む」かつ
    // ポートを伴う場合だけに限る。境界は ASCII だけで判定する（\w は日本語の文字も含み、「接続先127.0.0.1」を取り逃がす）。
    //
    // 🔴 FR-11, #984, IADR-0419（IADR-0405 決定2 の最後の項を置き換える）: 照合は **NonBacktracking**（入力長に線形。
    // .NET が保証する）で行い、**壁時計の予算を持たない**。旧形は 200 ms のマッチタイムアウトで打ち切り、理由文を定型文へ
    // 置き換えていたが、CPU が混むと数 ms の仕事でも 200 ms を超え、**台帳から理由文が黙って欠けた**（#984 の実測）。
    // NonBacktracking は前後読み・原子グループを持てないので、IADR-0405 決定2 の線引きを**同じ意味の形**へ書き換えた
    // （旧パターンとの差分試験 T-10-980 が固定する）:
    // - 語の先頭の条件（旧: 後読み）は、直前の 1 文字（または文字列の先頭）を**消費**して表す。伏せるのは組 `e` だけ。
    // - 語の終わりの条件（旧: 先読み）は、直後の 1〜2 文字（または文字列の末尾）を消費して表す。
    // - ホスト名の「英字を含む」（旧: 先読み）は、英字が最初のラベルにあるか後のラベルにあるかの 2 通りで表す。
    // 消費した前後の文字は隣の接続先の境界を兼ね得るため、Replace ではなく MaskEndpoints で照合を繰り返す。
    private const string UrlWordStart = @"(?:^|[^A-Za-z0-9+.\-])";
    private const string WordStart = @"(?:^|[^A-Za-z0-9_.\-])";
    private const string Label = @"[A-Za-z0-9\-]+";
    private const string HostWithLetter =
        @"(?:(?:" + Label + @"\.)+[0-9\-]*[A-Za-z][A-Za-z0-9\-]*(?:\." + Label + @")*"
        + @"|[0-9\-]*[A-Za-z][A-Za-z0-9\-]*(?:\." + Label + @")+)";

    private static readonly Regex Endpoint = new(
        UrlWordStart + @"(?<e>[A-Za-z][A-Za-z0-9+.\-]*://[^\s（）()「」<>""']+)"
        + @"|(?<e>\[[0-9A-Fa-f.]*:[0-9A-Fa-f:.]*\](?::\d{1,5})?)"
        + @"|" + WordStart + @"(?<e>\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?)(?:$|[^A-Za-z0-9_.]|\.(?:$|\D))"
        + @"|" + WordStart + @"(?<e>" + HostWithLetter + @":\d{1,5})(?:$|\D)"
        + @"|" + WordStart + @"(?<e>localhost:\d{1,5})(?:$|\D)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        // 既定（未指定）にすると、プロセスに REGEX_DEFAULT_MATCH_TIMEOUT が設定されたとき壁時計の予算が戻る。
        Regex.InfiniteMatchTimeout);

    /// <summary>自由記述欄の文面を整える。null は null のまま返す。</summary>
    public static string? Sanitize(string? text)
    {
        if (text is null)
            return null;

        var folded = FoldControlCharacters(MaskEndpoints(text));
        return Bound(folded);
    }

    // 接続先を伏せる。照合 1 回は探し始めから見つけた接続先の末尾の付近までを読み、次の照合は伏せた接続先の末尾の
    // 1 文字手前から始めるので、読む量の合計は入力長に比例する（#984 の実測: 2 万〜32 万文字の反復入力 17 種で比例）。
    private static string MaskEndpoints(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var start = 0;
        while (start < text.Length)
        {
            var match = Endpoint.Match(text, start);
            if (!match.Success)
                break;

            // 次の e は前の e と重ならない: e の最後の文字が次の e の先頭になり得るのは IPv6 の `[` だけで、`[` で終わる e は
            // URL だけ、URL の直後は区切り（空白・括弧・引用符）か文字列の末尾であり IPv6 の 2 文字目になれない。
            var endpoint = match.Groups["e"];
            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, endpoint.Index - copied).Append(EndpointPlaceholder);
            copied = endpoint.Index + endpoint.Length;
            // 伏せた接続先の最後の 1 文字は、次の接続先の「直前の 1 文字」を兼ね得る（例 `[::1]1.2.3.4`）。
            start = copied - 1;
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    private static string FoldControlCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasFolded = false;
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                if (!previousWasFolded)
                    builder.Append(' ');
                previousWasFolded = true;
                continue;
            }

            builder.Append(c);
            previousWasFolded = false;
        }

        return builder.ToString();
    }

    private static string Bound(string text)
    {
        if (text.Length <= MaxLength)
            return text;

        var cut = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return text[..cut] + "…";
    }
}
