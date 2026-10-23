using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Npgsql;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 T-10-3010, NFR, FR-10, #1446（親 #1423 M-2）, IADR-0575 決定 2: 決済の直列化（発注執行の専有 DB の advisory lock）を**実 PostgreSQL で**検証する。
//
// 単体の試験（ClosePositionSerializationTests）はプロセス内のセマフォで回しており、本番の直列化（`pg_advisory_xact_lock`）は
// InMemory では確かめられない。ここで固定するのは次の 4 つである。
//   1. 別のインスタンス（＝入れ替え中の新旧の Pod・別の接続）が同じ鍵を取ると、先発が外すまで待つ。
//   2. 違う鍵（銘柄・決済の方向）は待たない。
//   3. 待ちの上限（lock_timeout）を超えたら ClosePositionLockUnavailableException（決済を送らない側）。DB に届かないときも同じ。
//   4. ロックだけの接続で取る——外した後に先発が別の接続で書いた行を後発が読める（ストアの書き込みをロックの取引に入れない）。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する。
[Trait("Category", "Integration")]
public sealed class ClosePositionLockE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public ClosePositionLockE2ETests()
    {
        // NFR, MSP/ADR-0090 決定 1・2, IADR-0497 (#1200): 要る依存を得られなければ理由つきで skip する。
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);

        _postgres = E2EInfrastructure.UseExternal ? null : new PostgreSqlBuilder("postgres:16").Build();
    }

    public async ValueTask InitializeAsync()
    {
        if (_postgres is not null)
            await _postgres.StartAsync();

        _connectionString = E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private static readonly ClosePositionKey Aapl = new("AAPL", Market.UnitedStates, TradeSide.Sell);

    [Fact]
    public async Task T_10_3010_別の接続の同じ鍵は先発が外すまで待ち違う鍵は待たない()
    {
        var podA = new PostgresClosePositionLock(_connectionString);
        var podB = new PostgresClosePositionLock(_connectionString);

        var held = await podA.AcquireAsync(Aapl, CancellationToken.None);
        var waiting = podB.AcquireAsync(Aapl, CancellationToken.None);

        // 違う鍵は待たない（決済の方向・銘柄が違えば別の建玉）。
        await using (await podB.AcquireAsync(Aapl with { CloseSide = TradeSide.Buy }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }
        await using (await podB.AcquireAsync(Aapl with { Symbol = "MSFT" }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }

        await Task.Delay(500);
        waiting.IsCompleted.Should().BeFalse("🔴 先発が握っている間、別の Pod の同じ建玉の決済は待つ");

        await held.DisposeAsync();
        await using var acquired = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        acquired.Should().NotBeNull("先発が外したら取れる");
    }

    [Fact]
    public async Task T_10_3010_待ちの上限を超えてもDBに届かなくても取れない例外で送らない側へ倒れる()
    {
        var podA = new PostgresClosePositionLock(_connectionString);
        var impatient = new PostgresClosePositionLock(_connectionString, TimeSpan.FromMilliseconds(300));

        await using (await podA.AcquireAsync(Aapl, CancellationToken.None))
        {
            var act = () => impatient.AcquireAsync(Aapl, CancellationToken.None);
            var thrown = (await act.Should().ThrowAsync<ClosePositionLockUnavailableException>()).Which;
            thrown.Key.Should().Be(Aapl);
            thrown.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
        }

        // 外れた後は取れる（上限で諦めた側の接続はロックを残していない）。
        await using (await impatient.AcquireAsync(Aapl, CancellationToken.None)) { }

        var unreachable = new PostgresClosePositionLock(
            new NpgsqlConnectionStringBuilder(_connectionString) { Port = 1, Timeout = 2 }.ConnectionString);
        var down = () => unreachable.AcquireAsync(Aapl, CancellationToken.None);
        await down.Should().ThrowAsync<ClosePositionLockUnavailableException>("DB に届かなければ決済を送らない");
    }

    [Fact]
    public async Task T_10_3010_外した後は先発が別の接続で書いた行を後発が読める()
    {
        await using (var setup = new NpgsqlConnection(_connectionString))
        {
            await setup.OpenAsync();
            await using var create = new NpgsqlCommand(
                "CREATE TABLE IF NOT EXISTS close_lock_probe (id int PRIMARY KEY); TRUNCATE close_lock_probe;", setup);
            await create.ExecuteNonQueryAsync();
        }

        var podA = new PostgresClosePositionLock(_connectionString);
        var podB = new PostgresClosePositionLock(_connectionString);

        var held = await podA.AcquireAsync(Aapl, CancellationToken.None);
        var reader = Task.Run(async () =>
        {
            await using (await podB.AcquireAsync(Aapl, CancellationToken.None))
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT count(*) FROM close_lock_probe", conn);
                return (long)(await count.ExecuteScalarAsync())!;
            }
        });

        // 先発の「記録の保存」（ストアの書き込みは別の接続で、その都度コミットされる）。
        await using (var writer = new NpgsqlConnection(_connectionString))
        {
            await writer.OpenAsync();
            await using var insert = new NpgsqlCommand("INSERT INTO close_lock_probe VALUES (1)", writer);
            await insert.ExecuteNonQueryAsync();
        }

        await held.DisposeAsync();
        (await reader.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(1, "後発は先発の記録を読んでから数量を決める");
    }
}
