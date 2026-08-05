using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150: Stage 1 の稼働観測ログの EF 実装。
// 段階ゲートの「60 営業日」（期間カウント）を数える供給元である。
//
// **未記録は 0 日を返す。** 営業日数の 0 は「条件未充足＝昇格しない」に倒れるため、統制違反件数
// （#387・IADR-0148）のように未供給と 0 を区別する必要が無い（取引件数・IADR-0149 決定3 と同じ判断）。
//
// 算入可否は**記録時に純関数（Stage1SessionHypotheses）が決めた結果**を列に落とす
// （算入規則の単一情報源はドメインであり、SQL 側へ写さない）。
internal sealed class EfStage1TradingDayObservationStore(RiskManagementDbContext db) : IStage1TradingDayObservationStore
{
    public void CreditUptime(
        DateOnly sessionDateEasternTime,
        BrokerProvider provider,
        int observedMinuteOfDayEasternTime,
        int coveredMinutes)
    {
        var row = db.Stage1SessionUptimes.Find(sessionDateEasternTime, provider);
        var current = row is null
            ? new Stage1SessionUptime(sessionDateEasternTime, provider)
            : Map(row);

        var updated = current.Credit(observedMinuteOfDayEasternTime, coveredMinutes);
        if (updated == current)
        {
            // 逆行・再送で何も変わらないなら書かない（行の更新時刻だけが動くのを避ける）。
            return;
        }

        if (row is null)
        {
            db.Stage1SessionUptimes.Add(ToRow(updated));
        }
        else
        {
            row.LastObservedMinuteOfDayEasternTime = updated.LastObservedMinuteOfDayEasternTime;
            row.OperationalMinutesBeforeEarlyClose = updated.OperationalMinutesBeforeEarlyClose;
            row.OperationalMinutesBeforeRegularClose = updated.OperationalMinutesBeforeRegularClose;
            row.MeetsUptimeThreshold = Stage1SessionHypotheses.MeetsUptimeThreshold(updated);
            row.QualifiesTowardStage1 = Stage1SessionHypotheses.Qualifies(updated);
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }

    // 算入された「日数」であり行数ではない（同じ日に複数の発注先で稼働していても 1 日）。
    public int GetQualifiedTradingDayCount() =>
        db.Stage1SessionUptimes
            .Where(r => r.QualifiesTowardStage1)
            .Select(r => r.SessionDateEasternTime)
            .Distinct()
            .Count();

    // FR-20, SC-03, IADR-0142 決定3: paper 稼働により除外された日数。**算入された日は数えない**——
    // その日は除外されていない（画面の「paper 稼働により N 日を除外」という説明が嘘になる）。
    public int GetExcludedInternalPaperDayCount()
    {
        var qualifiedDates = db.Stage1SessionUptimes
            .Where(r => r.QualifiesTowardStage1)
            .Select(r => r.SessionDateEasternTime)
            .Distinct()
            .ToList();

        return db.Stage1SessionUptimes
            .Where(r => r.Provider == BrokerProvider.InternalPaper && r.MeetsUptimeThreshold)
            .Select(r => r.SessionDateEasternTime)
            .Distinct()
            .ToList()
            .Count(d => !qualifiedDates.Contains(d));
    }

    public void ResetWindow()
    {
        // 低頻度（段階遷移時のみ）のため一括読み出しで消す。ExecuteDelete を使わないのは、
        // テストの InMemory プロバイダが未対応で経路が分岐するためである（#386 / #387 の観測ログと同じ）。
        var rows = db.Stage1SessionUptimes.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        db.Stage1SessionUptimes.RemoveRange(rows);
        db.SaveChanges();
    }

    private static Stage1SessionUptime Map(Stage1SessionUptimeRow row) => new(
        row.SessionDateEasternTime,
        row.Provider,
        row.LastObservedMinuteOfDayEasternTime,
        row.OperationalMinutesBeforeEarlyClose,
        row.OperationalMinutesBeforeRegularClose);

    private static Stage1SessionUptimeRow ToRow(Stage1SessionUptime uptime) => new()
    {
        SessionDateEasternTime = uptime.SessionDateEasternTime,
        Provider = uptime.Provider,
        LastObservedMinuteOfDayEasternTime = uptime.LastObservedMinuteOfDayEasternTime,
        OperationalMinutesBeforeEarlyClose = uptime.OperationalMinutesBeforeEarlyClose,
        OperationalMinutesBeforeRegularClose = uptime.OperationalMinutesBeforeRegularClose,
        MeetsUptimeThreshold = Stage1SessionHypotheses.MeetsUptimeThreshold(uptime),
        QualifiesTowardStage1 = Stage1SessionHypotheses.Qualifies(uptime),
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}
