using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ReportService.Infrastructure.Persistence;

// FR-07, ADR-0068 決定1・2, #1373, IADR-0548 決定2（PR #1374 の AI レビュー 🟡）: 確定の保存の失敗のうち、
// **読み直して決め直してよい衝突**だけを見分ける。段 B はこの衝突の上に「なお効いている」の判定を載せるため、
// 衝突でない失敗（接続断・想定外の制約違反など）を再試行へ混ぜない。混ぜると原因が見えにくくなり、段 B の判定の前提も崩れる。
public static class ReportConfirmConflict
{
    // 写しの鍵（種別・通し番号）。同じ種別の確定が並行すると、後の保存がこれに当たる（＝通し番号の衝突）。
    public const string SequenceKeyName = "PK_report_confirmed_versions";

    // 写しの (会話キー, 版) の一意。同じ行の確定が並行し、相手が同じ版を先に写したときに当たる（＝版の衝突）。
    public const string VersionIndexName = "IX_report_confirmed_versions_PeriodKey_Version";

    private const string InMemoryProviderName = "Microsoft.EntityFrameworkCore.InMemory";

    /// <summary>
    /// <paramref name="error"/> が読み直して決め直してよい衝突か。
    /// <list type="bullet">
    /// <item>行の版の並行トークンの衝突（<see cref="DbUpdateConcurrencyException"/>）＝同じ行の確定の並行。</item>
    /// <item>Postgres の一意制約違反（23505）で、制約名が上の 2 つのどちらか。それ以外の制約・SQLSTATE は衝突ではない。</item>
    /// <item>🔴 <see cref="ArgumentException"/> は <b>InMemory プロバイダのときだけ</b>（試験の経路）。InMemory は鍵の重複を
    /// <see cref="ArgumentException"/>（「同じ鍵の項目が既にある」）で返し、制約名を持たないため。本番の Npgsql ではこの形にならない。</item>
    /// </list>
    /// </summary>
    public static bool IsConflict(Exception error, string? providerName) => error switch
    {
        DbUpdateConcurrencyException => true,
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg } =>
            pg.ConstraintName is SequenceKeyName or VersionIndexName,
        ArgumentException => providerName == InMemoryProviderName,
        _ => false,
    };
}
