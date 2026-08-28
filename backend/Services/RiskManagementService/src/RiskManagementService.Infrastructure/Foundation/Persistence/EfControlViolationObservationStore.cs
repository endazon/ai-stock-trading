using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148: 発注審査の観測ログの EF 実装。
// 段階ゲートの「統制違反 0 件」（クラス C 限定）を数える供給元である。
//
// **未記録は未供給（null）を返す。0 件を返さない。** 段階ゲートの他の入力と違い、違反件数の 0 は
// 「合格」を意味するため、記録が無いことを 0 と同一視すると供給元の不在が合格として読まれる（#387）。
//
// 算入可否（発注先の許可制）とクラス分けは**記録時に純関数が決めた結果**を列に落とす
// （ControlViolationAggregation / RejectionReasonClassification が単一情報源。SQL 側に分類規則を書かない）。
internal sealed class EfControlViolationObservationStore(RiskManagementDbContext db)
    : IControlViolationObservationStore
{
    public void Record(OrderScreeningObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // DecisionId で冪等（1 回の発注審査につき 1 行＝計上単位そのもの）。再送は先着を保つ。
        if (db.OrderScreeningObservations.Find(observation.DecisionId) is not null)
        {
            return;
        }

        db.OrderScreeningObservations.Add(new OrderScreeningObservationRow
        {
            DecisionId = observation.DecisionId,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Provider = observation.Provider,
            CountsTowardStage1 = ControlViolationAggregation.IsCounted(observation.Provider),
            IsControlViolation = ControlViolationAggregation.CountsAsViolation(observation),
        });
        db.SaveChanges();
    }

    public ControlViolationTally? GetTally()
    {
        // 算入対象の観測が 1 件も無ければ未供給。件数だけを見て 0 を返してはならない。
        if (!db.OrderScreeningObservations.Any(r => r.CountsTowardStage1))
        {
            return null;
        }

        return ControlViolationTally.Observed(
            db.OrderScreeningObservations.Count(r => r.CountsTowardStage1 && r.IsControlViolation));
    }

    public void ResetWindow()
    {
        // 低頻度（段階遷移時のみ）かつ小規模のため一括読み出しで消す。
        // ExecuteDelete を使わないのは、テストの InMemory プロバイダが未対応で経路が分岐するためである。
        var rows = db.OrderScreeningObservations.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        db.OrderScreeningObservations.RemoveRange(rows);
        db.SaveChanges();
    }
}
