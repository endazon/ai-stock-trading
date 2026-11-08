using Npgsql;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>NFR, FR-10, IADR-0645 決定 2: 発行元の確かめの再送の記録の置き場（構成 <c>MessageOrigin:ReplayStore</c>）。</summary>
public enum MessageOriginReplayStoreKind
{
    /// <summary>既定: プロセスの中だけ（再起動で消える。起動より前の発行は厳しめに扱う）。</summary>
    Memory = 0,

    /// <summary>受け手のサービスの DB（<c>ConnectionStrings:DefaultConnection</c>）に残す（再起動で消えない）。</summary>
    Database = 1,
}

/// <summary>
/// 🔴 NFR, FR-10, IADR-0645 決定 2: 処理し終えたメッセージ ID（発行元の印の ID）の永続の記録。窓（<c>MessageOrigin:MaxAge</c>）の間だけ持つ。
/// 読みの失敗は例外で返す（呼び手は処理を止める）。
/// </summary>
public interface IMessageOriginReplayStore
{
    /// <summary>ID が <paramref name="now"/> の時点で有効な記録として在るか。</summary>
    ValueTask<bool> ContainsAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>ID を <paramref name="expires"/> まで記録する（既に在れば遅い方の期限に揃える）。</summary>
    ValueTask RecordAsync(Guid id, DateTimeOffset expires, CancellationToken cancellationToken);

    /// <summary>期限を過ぎた記録を捨てる。</summary>
    ValueTask PruneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// 🔴 NFR, FR-10, IADR-0645 決定 2: PostgreSQL の置き場。受け手のサービスの DB の表 <see cref="Table"/>（EF の表とは別。最初の使用で作る）。
/// 接続文字列も値もログに出さない。
/// </summary>
public sealed class PostgresMessageOriginReplayStore : IMessageOriginReplayStore, IAsyncDisposable
{
    /// <summary>表の名前（既定のスキーマ）。</summary>
    public const string Table = "message_origin_handled";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _ensure = new(1, 1);
    private volatile bool _ensured;

    public PostgresMessageOriginReplayStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask<bool> ContainsAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _dataSource.CreateCommand(
            $"SELECT EXISTS (SELECT 1 FROM {Table} WHERE message_id = $1 AND expires_at > $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = now.ToUniversalTime() });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask RecordAsync(Guid id, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _dataSource.CreateCommand(
            $"INSERT INTO {Table} (message_id, expires_at) VALUES ($1, $2) "
            + $"ON CONFLICT (message_id) DO UPDATE SET expires_at = GREATEST({Table}.expires_at, EXCLUDED.expires_at)");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = expires.ToUniversalTime() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _dataSource.CreateCommand($"DELETE FROM {Table} WHERE expires_at < $1");
        command.Parameters.Add(new NpgsqlParameter { Value = now.ToUniversalTime() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (_ensured)
            return;
        await _ensure.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ensured)
                return;
            await using var command = _dataSource.CreateCommand(
                $"CREATE TABLE IF NOT EXISTS {Table} (message_id uuid PRIMARY KEY, expires_at timestamptz NOT NULL); "
                + $"CREATE INDEX IF NOT EXISTS ix_{Table}_expires_at ON {Table} (expires_at);");
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DuplicateTable)
            {
                // 同じ DB を使う別のレプリカが同時に作った（IF NOT EXISTS でも作成の競合はこの形で返る）。
            }

            _ensured = true;
        }
        finally
        {
            _ensure.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _ensure.Dispose();
        return _dataSource.DisposeAsync();
    }
}

/// <summary>NFR, FR-10, IADR-0645 決定 2: 再送の記録の置き場が読めなかった（このメッセージは処理しない。Wolverine の再試行・エラーキューへ）。</summary>
public sealed class MessageOriginReplayStoreUnavailableException(Exception inner)
    : Exception("発行元の確かめの再送の記録の置き場を読めないため、処理しません（再試行します）。", inner);
