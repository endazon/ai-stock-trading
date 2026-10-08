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
//
// #1224, IADR-0516: 監査台帳の記録（借株料・損切りの手法・強制買戻しの推定・自動縮小・為替の状態）は「報告可能になる瞬間」
// （ReportableAt。市場を持つ記録はセッションの大引けと記録の時刻の遅いほう、持たない記録は記録の時刻）が窓に入る報告書に載る。
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
        // Market は東証・米国の 2 値だけで、RegularClose はどちらにも値を返す——`??` の右辺は現状到達しない。
        // 列挙に市場が足されて RegularClose が追随しなかった場合の保険として、現地の暦日の終わりを大引けとみなす
        // （閉場の判定を遅い側へ倒す）。🔴 その場合のタイムゾーン（MarketHours.ZoneOf の既定＝米国東部）はリスク管理の
        // TradingDay（未知の市場は JST）と揃っていない。市場を足すときは RegularClose・ZoneOf・TradingDay を同時に直すこと。
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

    /// <summary>
    /// FR-06, 計画 ADR-0053 決定 2, #1224, IADR-0516 決定 1: 瞬間が窓 (ClosedAfter, ClosedUntil] に入るか。
    /// 連続する報告書の窓は端で接するため、どの瞬間も同じ種別の報告書のちょうど 1 つに入る。
    /// </summary>
    public bool Contains(DateTimeOffset instant) => instant > ClosedAfter && instant <= ClosedUntil;

    /// <summary>
    /// FR-06, #1224, IADR-0516 決定 1: 市場を持つ記録が<b>報告可能になる瞬間</b>——そのセッション（市場の現地取引日
    /// <paramref name="sessionDay"/>）の大引けと、記録の時刻 <paramref name="recordedAt"/> の遅いほう。
    /// <para>
    /// セッション中・大引け前の記録はそのセッションの約定と同じ報告書に載り、生成境界の後に記録されたもの（東証の夜の記録など）は
    /// 次の報告書に載る（生成の時点でまだ無い記録を、生成済みの報告書へ割り当てない）。
    /// </para>
    /// </summary>
    public static DateTimeOffset ReportableAt(Market market, DateOnly sessionDay, DateTimeOffset recordedAt)
    {
        var zone = MarketHours.ZoneOf(market);
        var close = CloseOf(sessionDay, MarketSessions.RegularClose(market) ?? TimeOnly.MaxValue, zone);
        return close > recordedAt ? close : recordedAt;
    }

    /// <summary>
    /// FR-06, #1224, IADR-0516 決定 1: 記録の時刻の現地取引日をセッションとみなした <see cref="ReportableAt(Market, DateOnly, DateTimeOffset)"/>。
    /// </summary>
    public static DateTimeOffset ReportableAt(Market market, DateTimeOffset recordedAt) =>
        ReportableAt(market, LocalDate(recordedAt, MarketHours.ZoneOf(market)), recordedAt);

    /// <summary>FR-06, #1224, IADR-0516 決定 1: 市場を持つ記録（記録の時刻の現地取引日をセッションとみなす）がこの窓の報告書に載るか。</summary>
    public bool Counts(Market market, DateTimeOffset recordedAt) => Contains(ReportableAt(market, recordedAt));

    /// <summary>
    /// FR-06, #1224, IADR-0516 決定 3: 監査台帳ほか JST の暦日で引く供給元へ渡す照会の範囲（窓を覆う JST の暦日の外包）。
    /// 下端は期間の始まり・窓の照会範囲の始まり（各市場の最初の現地取引日。その日の現地 0 時は同じ JST 日付以降にある）・
    /// ClosedAfter の JST 日付の最小、上端は期間の終わりと ClosedUntil の JST 日付の最大。受け取った後に <see cref="Counts(Market, DateTimeOffset)"/>
    /// ・<see cref="Contains"/> で絞る（照会の契約は変えない）。
    /// </summary>
    public (DateOnly From, DateOnly To) JstLedgerRange(DateOnly periodStart, DateOnly periodEnd)
    {
        var from = new[] { periodStart, QueryRange().From, JstDate(ClosedAfter) }.Min();
        var to = new[] { periodEnd, JstDate(ClosedUntil) }.Max();
        return (from, to);
    }

    private static DateOnly JstDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(9)).DateTime);

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static DateTimeOffset CloseOf(DateOnly day, TimeOnly close, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(close);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

// FR-06, #1224, IADR-0516 決定 5: セッションの窓に揃えない入力を引いた JST の暦日の範囲 [From, To]（「集計したセッション」の行に書き足す）。
public sealed record ReportCalendarDays(DateOnly From, DateOnly To);

// FR-06, 計画 ADR-0053 決定 3, #1172, IADR-0492 決定 6: 報告書が集計したセッションの範囲（1 市場ぶん・市場の現地取引日）。
// From > To は「窓にその市場のセッションが 1 つも無い」（描画は「なし」）。
public sealed record ReportSessionRange(Market Market, DateOnly From, DateOnly To)
{
    /// <summary>窓にその市場のセッションが 1 つ以上あるか。</summary>
    public bool HasSession => From <= To;
}
