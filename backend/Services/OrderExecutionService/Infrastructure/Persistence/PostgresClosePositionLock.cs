using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderExecutionService.Features.OrderExecution;

namespace OrderExecutionService.Infrastructure.Persistence;

// 🔴 NFR, FR-10, #1446（親 #1423 M-2）, IADR-0575 決定 2: 決済の直列化の本番実装（発注執行の専有 DB の advisory lock）。
//
// - **ロックだけを持つ専用の接続とトランザクション**で `pg_advisory_xact_lock` を取る。ストア（DbContext）の書き込みは別の接続で
//   その都度コミットされるので、ロックを外す前に後発が読める（READ COMMITTED）。ストアの書き込みをこのトランザクションに入れない
//   ——送信の後にロールバックされると、証券会社へ送った注文の記録が消える。
// - 外れるのはロールバック（ハンドルの破棄）・接続の切断・プロセスの停止のどれか。取り忘れ・外し忘れが無い。
// - 待ちの上限は `lock_timeout`（advisory lock にも効く）。上限を超えたら決済を送らない（各経路が「送っていない」で扱う）。
// - レプリカ・入れ替え中の新旧の Pod をまたいで効く（helm は replicas: 1 だが、RollingUpdate の間は 2 つが重なる）。
// - 関係 DB でないプロバイダ（試験の EF InMemory）はプロセス内のセマフォへ縮退する（IADR-0529 の新規建ての審査と同じ作法。
//   本番の配線は常に Npgsql）。選ぶのは ForContext だけである。
public sealed class PostgresClosePositionLock(string connectionString, TimeSpan? waitLimit = null) : IClosePositionLock
{
    // 🔴 1 ミリ秒未満は拒否する（0 へ丸めると lock_timeout が無期限になる）。
    private readonly TimeSpan _waitLimit = ClosePositionLockLimits.Validate(waitLimit ?? ClosePositionLockLimits.WaitLimit);

    /// <summary>
    /// 発注執行の DbContext の接続先で決済の直列化を選ぶ。関係 DB なら同じ接続文字列の advisory lock、そうでなければ
    /// <paramref name="nonRelationalFallback"/>（ホストの組み立てが DI の singleton で 1 つ渡す＝同じホストの経路どうしだけが直列になる）。
    /// </summary>
    public static IClosePositionLock ForContext(DbContext db, InProcessClosePositionLock nonRelationalFallback)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(nonRelationalFallback);
        if (!db.Database.IsRelational())
            return nonRelationalFallback;

        var connectionString = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("発注執行の DB の接続文字列が無いため、決済の直列化を組み立てられません。");
        return new PostgresClosePositionLock(connectionString);
    }

    public async Task<IAsyncDisposable> AcquireAsync(ClosePositionKey key, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // SET は値をパラメータに取れないため、整数のミリ秒を直に埋める（外からの入力ではない）。
            var timeoutMs = (long)Math.Ceiling(_waitLimit.TotalMilliseconds); // 1 以上（Validate が保証する）
            await using (var setTimeout = new NpgsqlCommand($"SET LOCAL lock_timeout = {timeoutMs}", connection, transaction))
                await setTimeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
            {
                acquire.Parameters.AddWithValue("key", key.AdvisoryKey);
                await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new Handle(connection, transaction);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DisposeQuietlyAsync(connection, transaction).ConfigureAwait(false);
            var reason = ex is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }
                ? $"{_waitLimit} 待っても外れませんでした"
                : "DB でロックを取れませんでした";
            throw new ClosePositionLockUnavailableException(key, reason, ex);
        }
        catch
        {
            await DisposeQuietlyAsync(connection, transaction).ConfigureAwait(false);
            throw;
        }
    }

    // 外す側の失敗は握る（ロールバックできなければ接続が壊れている＝サーバー側でセッションが終わればロックは外れる）。
    private static async ValueTask DisposeQuietlyAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        if (transaction is not null)
        {
            try
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 下の破棄で接続ごと閉じる。
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Handle(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _released, 1) == 0
                ? DisposeQuietlyAsync(connection, transaction)
                : ValueTask.CompletedTask;
    }
}
