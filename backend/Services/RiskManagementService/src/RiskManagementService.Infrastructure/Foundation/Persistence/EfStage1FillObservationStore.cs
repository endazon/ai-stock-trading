using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3, IADR-0149: Stage 1 の約定観測ログの EF 実装。
// 段階ゲートの「最小取引件数 100 件」を数える供給元である。
//
// **未記録は 0 件を返す。** 統制違反件数（#387・IADR-0148）と違い、取引件数の 0 は「条件未充足＝昇格しない」
// に倒れるため、未供給と 0 を区別する必要が無い。
//
// 算入可否は**記録時に純関数 Stage1Aggregation.CountsAsTrade が決めた結果**を列に落とす
// （算入規則の単一情報源はドメインであり、SQL 側へ写さない）。
internal sealed class EfStage1FillObservationStore(RiskManagementDbContext db) : IStage1FillObservationStore
{
    public void Record(Stage1FillObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // DecisionId で冪等（1 注文 1 行＝計上単位そのもの）。分割約定の続報・再送は先着を保つ。
        if (db.Stage1FillObservations.Find(observation.DecisionId) is not null)
        {
            return;
        }

        db.Stage1FillObservations.Add(new Stage1FillObservationRow
        {
            DecisionId = observation.DecisionId,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            SessionDateEasternTime = observation.SessionDateEasternTime,
            Provider = observation.Provider,
            PositionEffect = observation.PositionEffect,
            CountsTowardStage1 = Stage1Aggregation.CountsAsTrade(observation),
        });
        db.SaveChanges();
    }

    public int GetTradeCount() => db.Stage1FillObservations.Count(r => r.CountsTowardStage1);

    public void ResetWindow()
    {
        // 低頻度（段階遷移時のみ）のため一括読み出しで消す。ExecuteDelete を使わないのは、
        // テストの InMemory プロバイダが未対応で経路が分岐するためである（IADR-0148 の観測ログと同じ）。
        var rows = db.Stage1FillObservations.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        db.Stage1FillObservations.RemoveRange(rows);
        db.SaveChanges();
    }
}
