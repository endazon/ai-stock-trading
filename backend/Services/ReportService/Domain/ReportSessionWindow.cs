using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, UC-03〜05, 04_workflows/03_reporting-cycle, #1172, IADR-0492 決定 1・2: 報告書が集計する**セッションの窓**（純関数・決定的）。
//
// 報告書は「前の営業日の生成境界の後〜自分の生成境界まで」に**大引けを迎えたセッション**を集計する。セッションは
// （市場, 市場の現地取引日）で数え、約定・取り込みの取引日はリスク管理サービスと同じ市場の現地取引日
// （PortfolioProjection.TradeDate・IADR-0246 決定 2）で読む。
//
// 🔴 **報告書の期間（JST の営業日）を、そのまま各市場の取引日として照会しない。** 米国のセッション（ET 日 D）は
// JST の D 22:30〜D+1 05:00/06:00 にあり、16:00 JST の生成境界では ET 日 D はまだ始まってもいない。同じ日付で引くと
// 米国の約定は**どの日報にも載らない**（#1172）。
//
// 窓は半開区間 (ClosedAfter, ClosedUntil]。連続する営業日の窓は端で接し、重ならず隙間も無い——各セッションは
// 同じ種別の報告書のちょうど 1 つに入る（週報・月報の窓は、その期間の日報の窓の和に等しい）。
public sealed record ReportSessionWindow(DateTimeOffset ClosedAfter, DateTimeOffset ClosedUntil)
{
    /// <summary>窓を求める対象の市場（取引台帳が持ち得る市場のすべて）。</summary>
    public static IReadOnlyList<Market> Markets { get; } = Enum.GetValues<Market>();

    /// <summary>
    /// その市場で窓に入る現地取引日の範囲 [From, To]（大引けが窓に入る日）。入る日が無ければ From &gt; To。
    /// </summary>
    public (DateOnly From, DateOnly To) TradingDays(Market market)
    {
        var zone = MarketHours.ZoneOf(market);
        // 未知の市場は現地の暦日の終わりを大引けとみなす（閉場の判定を遅い側へ倒す）。
        var close = MarketSessions.RegularClose(market) ?? TimeOnly.MaxValue;

        var from = LocalDate(ClosedAfter, zone);
        if (CloseOf(from, close, zone) <= ClosedAfter)
            from = from.AddDays(1);

        var to = LocalDate(ClosedUntil, zone);
        if (CloseOf(to, close, zone) > ClosedUntil)
            to = to.AddDays(-1);

        return (from, to);
    }

    /// <summary>
    /// 取引台帳へ照会する取引日の範囲（全市場の <see cref="TradingDays"/> の和の外包）。照会の契約（市場の現地取引日で
    /// 絞る [from, to]）は変えず、外包で引いてから <see cref="Includes"/> で市場ごとに絞る。
    /// </summary>
    public (DateOnly From, DateOnly To) QueryRange()
    {
        var ranges = Markets.Select(TradingDays).ToList();
        return (ranges.Min(r => r.From), ranges.Max(r => r.To));
    }

    /// <summary>その市場のその瞬間（約定・取り込み）の現地取引日が窓に入るか。</summary>
    public bool Includes(Market market, DateTimeOffset instant)
    {
        var (from, to) = TradingDays(market);
        var day = LocalDate(instant, MarketHours.ZoneOf(market));
        return day >= from && day <= to;
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static DateTimeOffset CloseOf(DateOnly day, TimeOnly close, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(close);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
