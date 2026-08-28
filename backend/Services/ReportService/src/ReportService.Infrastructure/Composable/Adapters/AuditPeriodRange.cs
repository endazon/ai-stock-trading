namespace AiStockTrading.Report.Infrastructure.Composable.Adapters;

// FR-06, FR-11, #338, #381, IADR-0199 決定3: 監査台帳を期間で引くときの区間の作り方（純関数）。
//
// 🔴 **JST の暦日 → UTC の半開区間 [from 00:00 JST, to+1 日 00:00 JST)**。
// 台帳の OccurredAt は UTC 基準の瞬間であり、報告期間は JST の暦日である。
// **ここを取り違えると、日付境界の事象が隣の日の報告書へ落ちる。**
// 終端を 23:59:59 で閉じると**その日の最後の 1 秒が落ちる**。
//
// 🔴 **3 つ目の照会元（#338 の LLM 利用実績・借株料）が増えたため、区間の作り方を 1 箇所へ集約した。**
// 各アダプタが自前で書くと、**どれか 1 つで境界を間違えても他が正しいため気づかない**——
// ReportRenderer の AppendFxCredits を 3 出口すべてから呼ぶのと同じ理由である。
internal static class AuditPeriodRange
{
    /// <summary>報告書の生成境界と同じ時差（JST・UTC+9）。ReportSchedule.JstOffset と同値。</summary>
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    public static (DateTimeOffset From, DateTimeOffset To) JstHalfOpen(DateOnly fromInclusive, DateOnly toInclusive)
    {
        var from = new DateTimeOffset(fromInclusive.ToDateTime(TimeOnly.MinValue), Jst);
        var to = new DateTimeOffset(toInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue), Jst);
        return (from.ToUniversalTime(), to.ToUniversalTime());
    }
}
