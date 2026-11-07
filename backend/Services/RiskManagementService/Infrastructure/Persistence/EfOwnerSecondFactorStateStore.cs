using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// NFR-06, FR-14, IADR-0626 決定 1: 第 2 要素の状態の EF 実装（単一行・owner_second_factor_state）。
// 門（singleton）から呼ばれるので、呼ぶたびにスコープを作って DbContext を得る（要求のスコープの書き込みと混ぜない。
// 使ったコード・数えた失敗は、その後に操作が失敗しても残す）。
// 🔴 読み・決め・書くは版（並行トークン）で守る。並行する他方の書き込みと競合したら読み直して決め直す（上限 MaxAttempts 回）。
// 上限を超えた・DB に届かないなどは例外のまま返す（門は Unavailable として閉じる）。
public sealed class EfOwnerSecondFactorStateStore(IServiceScopeFactory scopes, TimeProvider time) : IOwnerSecondFactorStateStore
{
    public const int MaxAttempts = 5;

    public T Update<T>(string secretFingerprint, Func<OwnerSecondFactorState, (OwnerSecondFactorState Next, T Result)> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        for (var attempt = 1; ; attempt++)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RiskManagementDbContext>();
            try
            {
                var row = db.OwnerSecondFactorState.Find(SingletonKeys.Id);
                var current = row is not null && string.Equals(row.SecretFingerprint, secretFingerprint, StringComparison.Ordinal)
                    ? ToState(row)
                    : OwnerSecondFactorState.Initial;
                var (next, result) = decide(current);
                if (row is null)
                {
                    row = new OwnerSecondFactorStateRow { Id = SingletonKeys.Id, Version = 0 };
                    db.OwnerSecondFactorState.Add(row);
                }
                else
                {
                    row.Version++;
                }

                Apply(row, secretFingerprint, next, time.GetUtcNow());
                db.SaveChanges();
                return result;
            }
            catch (Exception ex) when (attempt < MaxAttempts && EfKillSwitchStore.IsConflict(ex))
            {
                // 読み直して決め直す（新しいスコープ＝新しい DbContext）。
            }
        }
    }

    private static OwnerSecondFactorState ToState(OwnerSecondFactorStateRow row) => new(
        row.LastAcceptedStep, row.ConsecutiveFailures, row.LockoutCount, row.LockedUntil, row.ManualUnlockRequired,
        row.LastAcceptedOperation, row.LastAcceptedTarget);

    private static void Apply(OwnerSecondFactorStateRow row, string fingerprint, OwnerSecondFactorState next, DateTimeOffset now)
    {
        row.SecretFingerprint = fingerprint;
        row.LastAcceptedStep = next.LastAcceptedStep;
        row.ConsecutiveFailures = next.ConsecutiveFailures;
        row.LockoutCount = next.LockoutCount;
        row.LockedUntil = next.LockedUntil;
        row.ManualUnlockRequired = next.ManualUnlockRequired;
        row.LastAcceptedOperation = next.LastAcceptedOperation;
        row.LastAcceptedTarget = next.LastAcceptedTarget;
        row.UpdatedAt = now;
    }
}
