using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, UC-06, IADR-0041/0070: 段階遷移台帳の EF 実装（追記専用）。段階ゲートは Stage 0（検証）を起点に始まり、
// 現在段階・次シーケンスは履歴の畳み込み（純ドメイン StageGateLedger）で導出する。DbContext は scoped のため本ストアも scoped。
public sealed class EfStageGateStore(RiskManagementDbContext db) : IStageGateStore
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

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException ex) when (IsSequenceUniqueViolation(ex))
        {
            // Sequence（主キー）の一意制約違反＝別要求が同シーケンスを先取り（並行二重承認・二重送信・リトライ）。
            // 楽観的整合の競合として扱い、ホスト層フィルタが 409（最新を取得して再試行）へ写像できるよう変換する
            // （IADR-0070 決定1。設定の DbUpdateConcurrencyException と同じ 409 経路に揃える）。
            db.ChangeTracker.Clear();
            throw new DbUpdateConcurrencyException(
                "段階遷移が他の更新と競合しました。最新の現在段階を取得して再試行してください。", ex);
        }
    }

    // Sequence 一意制約違反（PostgreSQL: 23505 unique_violation）のみを競合とみなす。接続断など他の書き込み失敗は
    // 誤って 409 に丸めず素通しする（正直な 500）。DbUpdateConcurrencyException（設定の楽観排他）はここでは扱わない。
    private static bool IsSequenceUniqueViolation(DbUpdateException ex) =>
        ex is not DbUpdateConcurrencyException
        && ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
