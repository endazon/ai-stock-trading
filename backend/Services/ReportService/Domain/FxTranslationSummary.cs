namespace ReportService.Domain;

// FR-06, FR-16, #338, 04_report-templates §数値の定義（為替差損益）・日報 §1・月報 §1:
// **為替差損益**（円換算により生じた損益）。
//
// 計画の明文: 「円換算により生じた損益。**取引損益と混ぜず独立した行として表示する**。
// リスク統制・Stage 昇格の判定は USD 建てで行うため、円建て表示は参考値である。」
//
// 🔴 **取引損益（PnlSummary）と同じ型に入れない。** 型を分けることで、
// 「独立した行として表示する」という計画の要求を**構造的に**満たす（合算する書き方ができない）。
//
// **`null`（本型そのものが無い）＝供給されていない。** 供給が無い期間を `0 円` と書くと
// 「為替では損得が無かった」と読める——`internal` の報告書を方針書として読む利用者を誤らせる。

/// <summary>
/// FR-16, #338: 為替差損益の 1 明細。基準通貨（USD）建ての金額を、
/// <b>認識時のレート</b>と<b>期末のレート</b>の両方で円換算し、その差を為替差損益とする。
/// </summary>
/// <param name="AmountBase">基準通貨建ての金額（USD）。</param>
/// <param name="RateAtRecognition">認識時の換算レート（1 USD あたりの円）。</param>
/// <param name="RateAtPeriodEnd">期末の換算レート（1 USD あたりの円）。</param>
public sealed record FxTranslationEntry(decimal AmountBase, decimal RateAtRecognition, decimal RateAtPeriodEnd);

/// <summary>
/// FR-16, #338: 為替差損益の集計結果。
/// </summary>
/// <param name="TranslationGainJpy">為替差損益（円）。プラスは円換算で得、マイナスは損。</param>
/// <param name="EntryCount">集計に用いた明細数。</param>
/// <param name="PeriodEndRate">
/// FR-06, #611, IADR-0285 決定5: 期末の再測定に用いた期末レート（1 USD あたりの円）。期末に建玉が残らず期末レートを
/// 使わなかった集計では <c>null</c>。報告書は値と観測日を併記し、「どの日の終値で再測定したか」を読み手が確かめられるようにする。
/// </param>
/// <param name="PeriodEndRateAsOf">期末レートの観測日（源の収録遅延により期末日以前になり得る）。</param>
public sealed record FxTranslationSummary(
    decimal TranslationGainJpy,
    int EntryCount,
    decimal? PeriodEndRate = null,
    DateOnly? PeriodEndRateAsOf = null);

// FR-16, #338, IADR-0251: 為替差損益の集計（純関数・決定的・副作用なし）。
// 🔴 **LLM に計算させない。** 本関数が唯一の算出経路であり、散文（Narrative）はこの値に触れない。
public static class FxTranslationAggregator
{
    /// <summary>
    /// 為替差損益 ＝ Σ 金額(USD) ×（期末レート − 認識時レート）。
    /// <para>
    /// レートが等しければ必ず 0 になる（＝為替の変動が無ければ為替差損益は生じない）。
    /// この不変条件はプロパティテストで固定する。
    /// </para>
    /// </summary>
    public static FxTranslationSummary Aggregate(IReadOnlyList<FxTranslationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var gain = 0m;
        foreach (var e in entries)
            gain += e.AmountBase * (e.RateAtPeriodEnd - e.RateAtRecognition);

        return new FxTranslationSummary(gain, entries.Count);
    }
}
