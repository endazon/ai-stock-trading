namespace ReportService.Domain;

// FR-06, UC-03, INDEX 決定事項16, IADR-0071 決定4: 初回月報ブートストラップ。
// 確定済み月報がまだ存在しない（＝運用開始直後）とき、初期監視銘柄を選定した月報ドラフトを純関数で生成する。
// これは提示用のドラフトであり、利用者の対話的確定を経て初めて方針として有効化される（ADR-0003）。
public static class MonthlyBootstrap
{
    public static TradingReport BuildDraft(DateOnly month, IReadOnlyList<string> watchlist, int assumptionsVersion)
    {
        ArgumentNullException.ThrowIfNull(watchlist);

        var periodStart = new DateOnly(month.Year, month.Month, 1);
        var symbols = watchlist.Count > 0 ? string.Join(", ", watchlist) : "（未選定）";

        return new TradingReport
        {
            PeriodKey = ReportPeriod.ExpectedKey(ReportKind.Monthly, periodStart),
            Kind = ReportKind.Monthly,
            PeriodStart = periodStart,
            State = ReportState.Draft,
            BasedOn = null,
            AssumptionsVersion = assumptionsVersion,
            PolicySummary = $"初回月報ブートストラップ: 当月の初期監視銘柄を選定する。候補: {symbols}。確定前は取引に適用されない。",
        };
    }

    /// <summary>
    /// FR-06, FR-09, UC-03, #839, IADR-0382: 提示通知（Discord）の要約。
    /// <para>
    /// 🔴 <b><see cref="PnlSummary"/> を使わない。</b> ブートストラップは集計を 1 つも持たない ——
    /// ゼロの <c>PnlSummary</c> で <see cref="ReportSummary.Build"/> を呼ぶと
    /// 「実現損益 0 USD ／ 取引 0 件」と<b>騙る</b>ことになる（本サービスが全節で避けている取り違えである）。
    /// </para>
    /// <para>
    /// 🔴 <b>方針文（<paramref name="policySummary"/>）はコード定数から組み立てた文字列であり</b>、
    /// LLM 出力も利用者入力も含まない（<see cref="BuildDraft"/>）。したがってサニタイズの対象にならない
    /// ——構成の監視銘柄だけが差し込まれる。
    /// </para>
    /// </summary>
    public static string PresentationSummary(string periodLabel, string policySummary) =>
        $"月報 {periodLabel}（承認待ち・初回ブートストラップ）\n"
        + "数値の集計・散文はありません（方針の起点だけを持つドラフトです。**集計 0 ではありません**）。\n"
        + policySummary;
}
