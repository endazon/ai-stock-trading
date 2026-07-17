using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-20, UC-06, IADR-0041/0070: 段階遷移台帳の EF 実装（追記専用）。段階ゲートは Stage 0（検証）を起点に始まり、
// 現在段階・次シーケンスは履歴の畳み込み（純ドメイン StageGateLedger）で導出する。DbContext は scoped のため本ストアも scoped。
internal sealed class EfStageGateStore(RiskManagementDbContext db) : IStageGateStore
{
    public StageGateLedger Load()
    {
        var history = db.StageTransitions
            .OrderBy(r => r.Sequence)
            .AsEnumerable()
            .Select(r => new StageTransition(
                r.Sequence, r.FromStage, r.ToStage, r.Kind, r.ApprovedBy, r.OccurredAtUtc, r.Reason))
            .ToList();

        return StageGateLedger.Empty(TradingStage.Stage0Verification) with { History = history };
    }

    public void Append(StageTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        db.StageTransitions.Add(new StageTransitionRow
        {
            Sequence = transition.Sequence,
            FromStage = transition.FromStage,
            ToStage = transition.ToStage,
            Kind = transition.Kind,
            ApprovedBy = transition.ApprovedBy,
            OccurredAtUtc = transition.OccurredAtUtc,
            Reason = transition.Reason,
        });
        db.SaveChanges();
    }
}
