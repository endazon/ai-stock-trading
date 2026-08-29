using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-20, #385, 06_daytrading-review §4.2, IADR-0150: Stage 1 の「その日の稼働分数」を観測から積み、
// **カレンダーを持たないまま**算入可否を判定する純ロジック。
//
// #333（IADR-0137 決定1）は「その日の実際の通常取引時間」を観測入力として受ける形を作った。
// 本ファイルはその**供給側**であり、次の 2 つを担う。
//   1. 稼働の観測（時刻の点）を稼働分数（区間）へ積む — Stage1SessionUptime
//   2. 分母を発明せずに §4.2 の 50% 規則を適用する — Stage1SessionHypotheses
//
// **カレンダーは持たない。** これは制約ではなく**裁定された設計**である
// （利用者裁定 2026-08-07・#407。計画 06_daytrading-review §4.2「分母と除外の判定に外部カレンダーを用いない」。
// 環流 feedback/20260804_fr20-stage1-session-calendar.md → 裁定済み。ADR-0022 決定3 も別件で同じ向き）。
// 実装が持つのは §4.2 が明記した**取引時間そのものの転記**（9:30 開始・390 分／210 分）と、曜日の算術だけである。
//
// 🔴 **祝日表・休場日リスト・外部カレンダーを足してはならない**（裁定違反）。
// `Stage1SessionUptimeTests.カレンダーを内蔵していない__同じ稼働なら曜日だけで結果が決まる` が
// **その禁止を機械的に守る装置**であり、3 年ぶんの全日付で算入可否が曜日だけで決まることを確認している。
// **このテストを消すと裁定違反を検知できなくなる。**

/// <summary>
/// FR-20, #385, §4.2: ある取引日・ある発注先の稼働観測の積み上げ（純関数の状態）。
/// <para>
/// 分数は<b>2 つの窓それぞれについて</b>積む。どちらの窓が「その日の実際の通常取引時間」かを実装は知らないため、
/// 両方を保持して <see cref="Stage1SessionHypotheses"/> が両方で判定する（IADR-0150 決定3）。
/// </para>
/// </summary>
/// <param name="SessionDateEasternTime">米国東部時間での取引日（§4.2 の判定基準時刻）。</param>
/// <param name="Provider">
/// FR-20, #334, IADR-0142 決定1: その稼働の<b>発注先</b>。<b>既定値を与えない</b>——省略できるようにすると
/// 書き忘れが「算入される側」へ倒れ、外部へ一度も発注していない稼働で合格証跡が積み上がる。
/// </param>
/// <param name="LastObservedMinuteOfDayEasternTime">
/// 直前に成功した観測の分（米国東部時間の 0 時からの分数）。<b>未観測は -1</b>。
/// 次の観測が「どこから」の区間を保証するかを決める基準であり、**再送・逆行を無視する鍵**でもある。
/// </param>
/// <param name="OperationalMinutesBeforeEarlyClose">9:30〜13:00 ET（半日取引日の窓）のうち稼働していた分数。</param>
/// <param name="OperationalMinutesBeforeRegularClose">9:30〜16:00 ET（通常日の窓）のうち稼働していた分数。</param>
public sealed record Stage1SessionUptime(
    DateOnly SessionDateEasternTime,
    BrokerProvider Provider,
    int LastObservedMinuteOfDayEasternTime = -1,
    int OperationalMinutesBeforeEarlyClose = 0,
    int OperationalMinutesBeforeRegularClose = 0)
{
    /// <summary>
    /// 観測 1 件を積む（純関数）。
    /// <para>
    /// 観測は「その時刻に稼働していた」ことしか語らない。区間へ広げる規則は次のとおりであり、
    /// **迷う場合はすべて「積まない」へ倒す**（IADR-0150 決定2）。
    /// </para>
    /// <list type="number">
    /// <item>初回（直前の成功観測が無い）は 0 分。**遡らない**——どこから稼働していたかを知らない。</item>
    /// <item>直前の成功観測からの経過が <paramref name="coveredMinutes"/> を超えていれば 0 分。
    /// **probe を 1 回でも落とした区間は稼働と見なさない**——落ちた原因が停止そのものかもしれない。</item>
    /// <item>逆行・同時刻（再送・順序前後）は 0 分。**冪等**であり同じ観測を二重に積まない。</item>
    /// <item>それ以外は区間 (直前, 今回] を 2 つの窓と交差させた分を積む。
    /// 時間外（9:30 前・16:00 後）は交差しないため積まれない（§4.2「プレ／アフターマーケットは含めない」）。</item>
    /// </list>
    /// </summary>
    /// <param name="observedMinuteOfDayEasternTime">観測時刻（米国東部時間の 0 時からの分数）。</param>
    /// <param name="coveredMinutes">
    /// その観測が遡って保証する最大の分数（＝probe の巡回間隔）。
    /// <see cref="MaximumCreditMinutesPerObservation"/> で上限クランプする——供給側の設定ミスや改変で
    /// 1 件の観測が 1 日を埋めることを構造的に防ぐ。
    /// </param>
    public Stage1SessionUptime Credit(int observedMinuteOfDayEasternTime, int coveredMinutes)
    {
        var observed = observedMinuteOfDayEasternTime;
        if (observed < 0 || observed <= LastObservedMinuteOfDayEasternTime)
        {
            // 逆行・再送は何も積まない。値も進めない（先着の記録を壊さない）。
            return this;
        }

        var covered = Math.Clamp(coveredMinutes, 0, MaximumCreditMinutesPerObservation);
        var previous = LastObservedMinuteOfDayEasternTime;
        var vouched = previous >= 0 && observed - previous <= covered;

        return this with
        {
            LastObservedMinuteOfDayEasternTime = observed,
            OperationalMinutesBeforeEarlyClose = OperationalMinutesBeforeEarlyClose
                + (vouched ? Overlap(previous, observed, Stage1SessionHypotheses.RegularSessionOpenMinuteOfDayEasternTime, Stage1SessionHypotheses.EarlyCloseMinuteOfDayEasternTime) : 0),
            OperationalMinutesBeforeRegularClose = OperationalMinutesBeforeRegularClose
                + (vouched ? Overlap(previous, observed, Stage1SessionHypotheses.RegularSessionOpenMinuteOfDayEasternTime, Stage1SessionHypotheses.RegularCloseMinuteOfDayEasternTime) : 0),
        };
    }

    /// <summary>
    /// 1 件の観測が遡って保証できる分数の上限 **30 分**。
    /// <para>
    /// これを超える間隔で probe を回すと観測は 1 件も積まれない（<see cref="Credit"/> の規則 2）。
    /// 「間隔を伸ばせば粗くとも積める」という緩い側の逃げ道を作らないための上限である。
    /// </para>
    /// </summary>
    public const int MaximumCreditMinutesPerObservation = 30;

    // 区間 (from, to] と窓 [start, end] の重なり（分）。負にはならない。
    private static int Overlap(int from, int to, int start, int end) =>
        Math.Max(0, Math.Min(to, end) - Math.Max(from, start));
}

/// <summary>
/// FR-20, #385, §4.2, IADR-0150 決定3: 「その日の実際の通常取引時間」を知らないまま算入可否を判定する。
/// <para>
/// <b>取り得る通常取引時間の仮説をすべて作り、すべてで 50% 以上稼働していたときにだけ算入する。</b>
/// 実際の通常取引時間は仮説のいずれかであるから、<b>真の規則が算入しない日を算入することは原理的に起きない</b>
/// （市場休場日を除く。後述）。逆に、真の規則なら算入される日を落とすことはある——過少計上であり
/// 昇格が遅れる方向、すなわち安全側である。
/// </para>
/// <para>
/// <b>塞げない穴（正直な記録）</b>: <b>市場の祝日を判別する手段が無い。</b> OpenD は市場が閉じていても接続を保つため、
/// 祝日の稼働は「稼働」として観測される。週末は曜日の算術で外れるが、祝日は外れない。
/// 埋めるには出典の無い祝日表を実装が抱えることになり、IADR-0137 決定1・ADR-0022 決定3 の裁定に反する。
/// 判定源は計画側の裁定待ちである（docs/blocked-tasks.md B-4 ／
/// feedback/20260805_fr20-stage1-market-holiday-exclusion.md）。
/// </para>
/// </summary>
public static class Stage1SessionHypotheses
{
    /// <summary>
    /// 通常取引時間の開始 **9:30 ET**（＝0 時からの 570 分）。§4.2 の「9:30〜16:00 ET／9:30〜13:00 ET」の転記であり、
    /// **どの日がどちらかを述べるものではない**（カレンダーではない）。
    /// </summary>
    public const int RegularSessionOpenMinuteOfDayEasternTime = 9 * 60 + 30;

    /// <summary>半日取引日の引け **13:00 ET**（開始 ＋ 210 分。§4.2 の転記）。</summary>
    public const int EarlyCloseMinuteOfDayEasternTime =
        RegularSessionOpenMinuteOfDayEasternTime + Stage1DayQualification.RegularSessionMinutesHalfDay;

    /// <summary>通常日の引け **16:00 ET**（開始 ＋ 390 分。§4.2 の転記）。</summary>
    public const int RegularCloseMinuteOfDayEasternTime =
        RegularSessionOpenMinuteOfDayEasternTime + Stage1DayQualification.RegularSessionMinutesFullDay;

    /// <summary>
    /// その日に取り得る通常取引時間の仮説を、既存の判定入力（<see cref="Stage1TradingDayObservation"/>）として並べる。
    /// <para>
    /// **週末は「分母 0」の仮説 1 つ**である。これは <see cref="DayOfWeek"/> の算術であって
    /// カレンダー（日付の表）ではない——年ごとに変わらず、更新も要らず、誤りようが無い。
    /// 分母 0 は既存の「市場休場日は算入しない」経路（<see cref="Stage1DayQualification.UptimeRatio"/>）へそのまま乗る。
    /// </para>
    /// <para>
    /// 平日は<b>半日取引日（210 分）と通常日（390 分）の 2 仮説</b>である。祝日（0 分）を仮説に加えないのは、
    /// 加えるとどの日も決して算入されなくなり、ゲートそのものが成立しないためである。
    /// </para>
    /// <para>
    /// 🔴 <b>祝日を判別しないのは「まだ判定源が無い」からではなく、そう決まったからである</b>
    /// （利用者裁定 2026-08-07・質問票 第13回 Q3 案2。計画 06_daytrading-review §4.2「分母と除外の判定源」）。
    /// 裁定は<b>「祝日は判別しない。除外しない」</b>と定め、<b>祝日判定・休場日リスト・外部カレンダーを
    /// 入れること自体を禁じている</b>。したがって<b>ここへ祝日表を足すことは裁定違反である</b>。
    /// </para>
    /// <para>
    /// <b>帰結（裁定が明示した割り切り）</b>: 祝日に OpenD が稼働していた日は営業日として算入され、
    /// <b>60 営業日あたり年 2〜3 日を過大計上し得る。これは「昇格が早まる」側であり fail-safe ではない。</b>
    /// ただし影響は限定的である —— 60 営業日が 57〜58 営業日相当になる程度であり、
    /// 昇格には取引件数（§4.1 条件3）と<b>利用者承認</b>も要る。
    /// 一方<b>半日取引日は過少計上（安全側）へ倒れる</b>（2 仮説の両方で 50% を求めるため）。
    /// <b>安全側の割り切りだと誤読すると、後から祝日カレンダーを足す動機になる</b>ため、向きを明記しておく。
    /// </para>
    /// </summary>
    public static IReadOnlyList<Stage1TradingDayObservation> Hypotheses(Stage1SessionUptime uptime)
    {
        ArgumentNullException.ThrowIfNull(uptime);

        if (uptime.SessionDateEasternTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return [new Stage1TradingDayObservation(uptime.SessionDateEasternTime, 0, 0, uptime.Provider)];
        }

        return
        [
            new Stage1TradingDayObservation(
                uptime.SessionDateEasternTime,
                Stage1DayQualification.RegularSessionMinutesHalfDay,
                uptime.OperationalMinutesBeforeEarlyClose,
                uptime.Provider),
            new Stage1TradingDayObservation(
                uptime.SessionDateEasternTime,
                Stage1DayQualification.RegularSessionMinutesFullDay,
                uptime.OperationalMinutesBeforeRegularClose,
                uptime.Provider),
        ];
    }

    /// <summary>
    /// その日が<b>稼働率の条件</b>（どの仮説でも 50% 以上）を満たすか。発注先は見ない
    /// （<see cref="Stage1DayQualification.MeetsUptimeThreshold"/> と同じ役割分担）。
    /// </summary>
    public static bool MeetsUptimeThreshold(Stage1SessionUptime uptime) =>
        Hypotheses(uptime).All(Stage1DayQualification.MeetsUptimeThreshold);

    /// <summary>
    /// FR-06, FR-20, #569, 04_report-templates 日報 §1 / 月報 §6.2: <b>報告書へ載せる稼働率</b>
    /// （仮説の<b>最小値</b>）。
    /// <para>
    /// 🔴 <b>報告書側は分母を発明しない</b>（報告書サービス側の稼働率レコード（<c>OpenDUptimeRecord</c>）の明文）。
    /// 分母を知る唯一の場所は本クラスであり、しかしその日の実際の通常取引時間は
    /// <b>実装が知らない</b>（<see cref="Hypotheses"/>）。そこで <b>2 仮説の最小値</b>を返す。
    /// </para>
    /// <para>
    /// 最小値を選ぶ理由は<b>算入可否と食い違わせないため</b>である。
    /// <c>Min(ratio) &gt;= 0.50</c> と <see cref="MeetsUptimeThreshold"/>（＝<b>すべての</b>仮説で 50% 以上）は
    /// <b>恒等に一致する</b>。報告書は比率だけを受け取って算入可否を導く
    /// （<c>OpenDUptimeAggregator.IsCounted</c>）ため、最大値・平均を返すと
    /// <b>「算入」と描いた日が権威源では算入されていない</b>という食い違いが生じる。
    /// </para>
    /// <para>週末は分母 0 の仮説 1 つであり、稼働率は 0 になる（<see cref="Stage1DayQualification.UptimeRatio"/>）。</para>
    /// </summary>
    public static decimal UptimeRatio(Stage1SessionUptime uptime)
    {
        ArgumentNullException.ThrowIfNull(uptime);
        return Hypotheses(uptime).Min(Stage1DayQualification.UptimeRatio);
    }

    /// <summary>
    /// その日を Stage 1 の営業日 1 日として算入するか。稼働率の条件に加えて、
    /// <b>発注先が算入対象（moomoo <c>SIMULATE</c>）であること</b>を要する（許可制・IADR-0142 決定2）。
    /// </summary>
    public static bool Qualifies(Stage1SessionUptime uptime) =>
        Hypotheses(uptime).All(Stage1DayQualification.Qualifies);

    /// <summary>
    /// 観測の並びから算入日数を数える。<b>取引日で重複排除する</b>——同じ日に複数の発注先で稼働していても
    /// 1 日は 1 日である（§4.2 は日数を数えており、発注先ごとの延べ日数ではない）。
    /// </summary>
    public static int CountQualifiedDays(IEnumerable<Stage1SessionUptime> uptimes)
    {
        ArgumentNullException.ThrowIfNull(uptimes);
        return uptimes.Where(Qualifies).Select(u => u.SessionDateEasternTime).Distinct().Count();
    }

    /// <summary>
    /// FR-20, SC-03, IADR-0142 決定3: <b>内蔵 <c>paper</c> 稼働により除外された営業日数</b>。
    /// 数えるのは「発注先が内蔵 <c>paper</c> であり、<b>かつ稼働率の条件は満たしていた</b>日」だけである
    /// （休場日・稼働不足の日はもともと算入されない日であり、<c>paper</c> を理由に除外されたわけではない）。
    /// <b>同じ日に <c>SIMULATE</c> でも稼働して算入された日は数えない</b>——その日は除外されていない。
    /// </summary>
    public static int CountExcludedInternalPaperDays(IEnumerable<Stage1SessionUptime> uptimes)
    {
        ArgumentNullException.ThrowIfNull(uptimes);

        var materialized = uptimes as IReadOnlyCollection<Stage1SessionUptime> ?? [.. uptimes];
        var qualifiedDates = materialized.Where(Qualifies).Select(u => u.SessionDateEasternTime).ToHashSet();

        return materialized
            .Where(u => u.Provider == BrokerProvider.InternalPaper && MeetsUptimeThreshold(u))
            .Select(u => u.SessionDateEasternTime)
            .Distinct()
            .Count(d => !qualifiedDates.Contains(d));
    }
}
