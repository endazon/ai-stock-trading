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
/// 🔴 NFR, FR-10, IADR-0645 決定 2: PostgreSQL の置き場。受け手のサービスの DB の表 <see cref="Table"/>（EF の表とは別）。
/// <para>
/// 🔴 表は<b>起動時の移行の経路</b>（各サービスの Program.cs の <c>MigrateAsync</c> の直後の
/// <see cref="MessageOriginReplayStoreStartup.EnsureMessageOriginReplayStoreAsync"/>）で作る。EF の移行と同じ利用者（その DB の所有者）で作るので、
/// 移行が通る権限があれば作れる。作れなければ起動で止める（購読を始める前。メッセージを受けてから落とさない）。
/// 実行時は表を作らない（作っていない置き場の読み書きは例外＝処理せずに再試行）。
/// </para>
/// DI の Singleton としてホストが破棄する（<see cref="NpgsqlDataSource"/> を閉じる）。接続文字列も値もログに出さない。
/// </summary>
public sealed class PostgresMessageOriginReplayStore : IMessageOriginReplayStore, IAsyncDisposable
{
    /// <summary>表の名前（既定のスキーマ）。</summary>
    public const string Table = "message_origin_handled";

    private readonly NpgsqlDataSource _dataSource;
    private volatile bool _ensured;

    public PostgresMessageOriginReplayStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>起動時に表を作り終えたか。</summary>
    public bool IsReady => _ensured;

    /// <summary>
    /// 表を作る（冪等）。権限・接続の失敗は <see cref="MessageOriginReplayStoreSetupException"/>（起動で止める。SQLSTATE だけを載せる）。
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = _dataSource.CreateCommand(
                $"CREATE TABLE IF NOT EXISTS {Table} (message_id uuid PRIMARY KEY, expires_at timestamptz NOT NULL); "
                + $"CREATE INDEX IF NOT EXISTS ix_{Table}_expires_at ON {Table} (expires_at);");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DuplicateTable)
        {
            // 同じ DB を使う別のレプリカが同時に作った（IF NOT EXISTS でも作成の競合はこの形で返る）。
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MessageOriginReplayStoreSetupException(ex);
        }

        _ensured = true;
    }

    public async ValueTask<bool> ContainsAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowIfNotReady();
        await using var command = _dataSource.CreateCommand(
            $"SELECT EXISTS (SELECT 1 FROM {Table} WHERE message_id = $1 AND expires_at > $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = now.ToUniversalTime() });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask RecordAsync(Guid id, DateTimeOffset expires, CancellationToken cancellationToken)
    {
        ThrowIfNotReady();
        await using var command = _dataSource.CreateCommand(
            $"INSERT INTO {Table} (message_id, expires_at) VALUES ($1, $2) "
            + $"ON CONFLICT (message_id) DO UPDATE SET expires_at = GREATEST({Table}.expires_at, EXCLUDED.expires_at)");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = expires.ToUniversalTime() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ThrowIfNotReady();
        await using var command = _dataSource.CreateCommand($"DELETE FROM {Table} WHERE expires_at < $1");
        command.Parameters.Add(new NpgsqlParameter { Value = now.ToUniversalTime() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfNotReady()
    {
        if (!_ensured)
            throw new InvalidOperationException(
                "再送の記録の表を起動時に作っていません（Program.cs の MigrateAsync の直後に EnsureMessageOriginReplayStoreAsync を呼ぶ）。");
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

/// <summary>NFR, FR-10, IADR-0645 決定 2: 起動時の移行の経路で、再送の記録の表を作る。</summary>
public static class MessageOriginReplayStoreStartup
{
    /// <summary>
    /// 🔴 置き場が Database なら表を作る（Memory なら何もしない）。DB を持つ受け手の Program.cs が <c>MigrateAsync</c> の直後に呼ぶ
    /// （ホストの起動＝購読の開始より前）。作れなければ <see cref="MessageOriginReplayStoreSetupException"/> で起動を止める。
    /// </summary>
    public static Task EnsureMessageOriginReplayStoreAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService(typeof(PostgresMessageOriginReplayStore)) is PostgresMessageOriginReplayStore store
            ? store.EnsureCreatedAsync(cancellationToken)
            : Task.CompletedTask;
    }
}

/// <summary>NFR, FR-10, IADR-0645 決定 2: 再送の記録の表を起動時に作れなかった（権限・接続。起動で止める）。</summary>
public sealed class MessageOriginReplayStoreSetupException(Exception inner)
    : Exception(
        "発行元の確かめの再送の記録の表（" + PostgresMessageOriginReplayStore.Table + "）を作れないため起動しません"
        + (inner is PostgresException pg ? "（SQLSTATE " + pg.SqlState + "）" : "")
        + "。サービスの DB の利用者に既定のスキーマへの CREATE の権限（EF の移行と同じ）があるかを確かめるか、"
        + "MessageOrigin:ReplayStore を Memory に戻す。", inner);

/// <summary>NFR, FR-10, IADR-0645 決定 2: 再送の記録の置き場が読めなかった（このメッセージは処理しない。Wolverine の再試行・エラーキューへ）。</summary>
public sealed class MessageOriginReplayStoreUnavailableException(Exception inner)
    : Exception("発行元の確かめの再送の記録の置き場を読めないため、処理しません（再試行します）。", inner);
