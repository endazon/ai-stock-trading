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
                r.Sequence, r.FromStage, r.ToStage, r.Kind, r.ApprovedBy, r.OccurredAtUtc, r.Reason,
                // FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: verdict の行だけが添付を持つ。
                // 段階遷移の行は 2 列とも null であり、null のまま復元する（偽の添付を発明しない）。
                r.ShortSellReleaseSourceFingerprint is null || r.ShortSellReleaseStrategyId is null
                    ? null
                    : new ShortSellReleaseAttestation(
                        r.ShortSellReleaseSourceFingerprint, r.ShortSellReleaseStrategyId)))
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
            ShortSellReleaseSourceFingerprint = transition.ShortSellRelease?.SourceFingerprint,
            ShortSellReleaseStrategyId = transition.ShortSellRelease?.StrategyId,
        });

        try
        {
            db.SaveChanges();
        }
        // FR-20, #714, IADR-0317, IADR-0319: **競合の判定は例外の型ではなく「その Sequence の行が実在するか」で行う。**
        //
        // 従来は `IsSequenceUniqueViolation`（`Npgsql.PostgresException { SqlState: "23505" }`）で絞っていた。
        // 意図（Sequence 一意制約違反のみを競合とみなし、接続断など他の失敗は 409 に丸めず素通しする）は
        // 正しいが、**判定が特定プロバイダの内部例外に固定**されており、InMemory・他プロバイダでは
        // 常に false になって競合が 409 へ変換されなかった（InMemory は ArgumentException を投げる）。
        // **意図はそのまま、判定条件だけを「行の実在」へ移す。**
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.ChangeTracker.Clear();

            if (!db.StageTransitions.AsNoTracking().Any(r => r.Sequence == transition.Sequence))
            {
                // その Sequence の行が生まれていない＝一意制約違反ではなく本物の書き込み失敗である。
                // 誤って 409 に丸めず素通しする（正直な 500）。
                throw;
            }

            // Sequence（主キー）の行が既に在る＝別要求が同シーケンスを先取り（並行二重承認・二重送信・リトライ）。
            // 楽観的整合の競合として扱い、ホスト層フィルタが 409（最新を取得して再試行）へ写像できるよう変換する
            // （IADR-0070 決定1。設定の DbUpdateConcurrencyException と同じ 409 経路に揃える）。
            throw new DbUpdateConcurrencyException(
                "段階遷移が他の更新と競合しました。最新の現在段階を取得して再試行してください。", ex);
        }
    }
}
