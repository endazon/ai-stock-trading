namespace AiStockTrading.Shared.Contracts.Trading;

// FR-06, FR-10, #381, ADR-0022 決定1, IADR-0196 決定4: 為替の情報源が要求するクレジット表記。
//
// 🔴 **文字列をここ 1 箇所に置く。** 取得側（TradeDecisionService のアダプタ）と表示側
// （ReportService の報告書）の両方が要るため、どちらかに置くともう一方が**書き写す**ことになる。
// 書き写すと、利用条件が変わったときに**片方だけ古くなり、しかも誰も気づかない**
// （表示だけが古い＝出典の記載が事実と違う、という最も気づきにくい壊れ方をする）。
//
// **契約（Shared.Contracts）に置くのが妥当である。** これは実装の詳細ではなく、
// 「この情報源を使うなら、この文言を出す」という**外部との約束**だからである。
public static class FxSourceCredits
{
    /// <summary>
    /// 日本銀行 時系列統計データ検索サイトの利用条件が求めるクレジット表記（ADR-0022 決定1）。
    /// <para>
    /// 商用利用しないため事前相談は不要（2026-08-05 利用者裁定）。
    /// <b>ただし報告書への出典明記の方針は維持する</b>——本定数はその方針の実体である。
    /// </para>
    /// </summary>
    public const string Boj = "このサービスは、日本銀行時系列統計データ検索サイトのAPI機能を使用しています";

    /// <summary>
    /// 情報源の識別子（イベントの <c>SourceName</c> ／構成の <c>Fx:Provider</c>）。
    /// <para>
    /// 🔴 <b>クレジット文言と同じ理由でここに置く。</b> 取得側は自分の名前を、表示側は
    /// <b>イベントに載ってきた名前</b>を見る——**別々に書くと、名前を変えたときに
    /// 出典だけが出なくなる**（表示が静かに欠ける最も気づきにくい形）。
    /// </para>
    /// </summary>
    public const string BojSourceName = "boj";

    /// <summary>
    /// 情報源の識別子に対して<b>出すべきクレジット</b>を返す。要求の無い源は <c>null</c>。
    /// <para>
    /// 🔴 <b>「使ったと分かっている源」にだけ使うこと</b>（IADR-0196 決定4・IADR-0199 決定5）。
    /// 使っていない源のクレジットを出すのは事実に反する。
    /// </para>
    /// </summary>
    public static string? ForSource(string? sourceName) =>
        string.Equals(sourceName, BojSourceName, StringComparison.OrdinalIgnoreCase) ? Boj : null;
}
