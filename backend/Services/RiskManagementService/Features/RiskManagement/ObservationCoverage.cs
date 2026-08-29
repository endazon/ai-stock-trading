
namespace RiskManagementService.Features.RiskManagement;

// FR-21, FR-10, FR-06, UC-06, #463, IADR-0181:
// **報告期間が観測の届いた取引日で覆われているか**の判定（純関数）。
//
// 計画（FR-21・2026-08-08 改定）:
// > **報告期間が観測の届いた取引日で覆われている場合に限り、件数 0 を正当な 0 として供給する。**
//
// 塞ぐ失敗モードは 2 つである（計画が受け入れ基準へ明記した）。
//   1. **初回観測より前の期間**（例: 初回観測が 8/20 で 7 月分の月報を生成する）
//   2. **観測が途中で止まった期間**（ブローカ接続の障害等）
//
// いずれも従前の「最終観測時刻が非 null なら供給」では**素通りしていた**。
public static class ObservationCoverage
{
    /// <summary>
    /// 期間 [fromInclusive, toInclusive] の**営業日がすべて**観測されているか。
    /// <para>
    /// **1 日でも欠ければ覆われていない**（＝未供給へ倒す）。部分的な観測を「覆っている」と扱うと、
    /// 観測が止まっていた日の推定 0 件が正当な 0 として報告され、失敗モード 2 が残る。
    /// </para>
    /// <para>
    /// **営業日の判定は暦に委ねる。** 祝日を含む暦がまだ無い（[#407]）ため、現在の暦（週末のみ除外）では
    /// 祝日が「観測されていない営業日」として現れ、**未供給へ倒れる**——
    /// これは**安全側**の誤りであり、逆（覆っていないのに供給）にはならない。
    /// </para>
    /// <para>期間が逆順（from &gt; to）なら覆っていないものとして扱う（呼び出し側が 400 で弾く前提の二重防御）。</para>
    /// </summary>
    public static bool Covers(
        IReadOnlyList<DateOnly> observedDays,
        DateOnly fromInclusive,
        DateOnly toInclusive,
        IBusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(observedDays);
        ArgumentNullException.ThrowIfNull(calendar);

        if (fromInclusive > toInclusive)
        {
            return false;
        }

        var observed = observedDays.ToHashSet();
        var businessDays = 0;

        for (var day = fromInclusive; day <= toInclusive; day = day.AddDays(1))
        {
            if (!calendar.IsBusinessDay(day))
            {
                continue;
            }

            businessDays++;
            if (!observed.Contains(day))
            {
                return false;
            }
        }

        // **営業日が 1 日も無い期間は覆えない。** 週末だけの期間で「覆っている」と返すと、
        // その期間の推定 0 件が正当な 0 になる——観測が一度も無くても成立してしまう。
        return businessDays > 0;
    }
}
