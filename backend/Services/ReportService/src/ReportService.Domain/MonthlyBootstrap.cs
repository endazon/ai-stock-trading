namespace AiStockTrading.Report.Domain;

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
}
