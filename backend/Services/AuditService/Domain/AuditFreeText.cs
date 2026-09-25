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
    // 🔴 各選択肢は語の先頭（ASCII の語の途中ではない位置）からしか始まらず、語の中は原子グループで読む——
    // 任意長の入力（例外メッセージ）でバックトラックが 2 乗に膨らまないようにするため（1 万文字の入力で
    // 素朴な形がタイムアウトすることを実測して決めた）。
    private static readonly Regex Endpoint = new(
        @"(?<![A-Za-z0-9+.\-])[A-Za-z](?>[A-Za-z0-9+.\-]*)://[^\s（）()「」<>""']+"
        + @"|\[[0-9A-Fa-f.]*:[0-9A-Fa-f:.]*\](?::\d{1,5})?"
        + @"|(?<![A-Za-z0-9_.\-])\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?(?![A-Za-z0-9_]|\.\d)"
        + @"|(?<![A-Za-z0-9_.\-])(?=[A-Za-z0-9.\-]*[A-Za-z])(?>[A-Za-z0-9\-]+)(?:\.(?>[A-Za-z0-9\-]+))+:\d{1,5}(?!\d)"
        + @"|(?<![A-Za-z0-9_.\-])localhost:\d{1,5}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(200));

    /// <summary>自由記述欄の文面を整える。null は null のまま返す。</summary>
    public static string? Sanitize(string? text)
    {
        if (text is null)
            return null;

        string masked;
        try
        {
            masked = Endpoint.Replace(text, EndpointPlaceholder);
        }
        catch (RegexMatchTimeoutException)
        {
            // 伏せ字を保証できない入力は中身を載せない（台帳へ残せないことだけを残す）。
            return "（理由文は整形できなかったため記録しない）";
        }

        var folded = FoldControlCharacters(masked);
        return Bound(folded);
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
