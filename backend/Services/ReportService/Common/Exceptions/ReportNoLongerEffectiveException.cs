namespace ReportService.Common.Exceptions;

// FR-07, FR-14, ADR-0068 決定1, #1373, IADR-0549 決定2: 確定済みの日報に版を足すドラフト（版 n+1）は、その日報が**なお効いている**
// （最新の確定済み日報である）間だけ作れ、確定できる。より新しい日付の日報が確定して効いている日報が移った後は断る。
// ドラフトは記録として残す（削除しない）。エンドポイントは 409 へ写す（理由の文言をそのまま返す）。
public sealed class ReportNoLongerEffectiveException(string periodKey, string? effectivePeriodKey)
    : Exception(MessageFor(periodKey, effectivePeriodKey))
{
    public string PeriodKey { get; } = periodKey;

    public string? EffectivePeriodKey { get; } = effectivePeriodKey;

    public static string MessageFor(string periodKey, string? effectivePeriodKey) =>
        effectivePeriodKey is null
            ? $"日報 {periodKey} は効いている日報ではないため、版を足して再確定できません。"
            : $"日報 {periodKey} は効いている日報ではありません（いま効いているのは {effectivePeriodKey}）。"
                + "より新しい日付の日報が確定したため、この日報に版を足して再確定することはできません（ドラフトは記録として残ります）。";
}
