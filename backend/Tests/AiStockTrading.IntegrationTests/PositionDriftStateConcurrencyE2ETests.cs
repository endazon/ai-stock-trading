extern alias RiskManagementWorker;

using System.Data;
using AiStockTrading.RiskManagement.Application.Ports;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using RiskManagementDbContext = RiskManagementWorker::AiStockTrading.RiskManagement.Worker.Foundation.Persistence.RiskManagementDbContext;
using EfPositionDriftStateStore = RiskManagementWorker::AiStockTrading.RiskManagement.Worker.Foundation.Persistence.EfPositionDriftStateStore;

namespace AiStockTrading.IntegrationTests;

// #305 / IADR-0124, FR-05/FR-10: 建玉乖離の追跡状態ストアの**並行制御を実 PostgreSQL で**検証する。
//
// ユニット試験は EF Core InMemory provider で並行トークンを検証しているが、本 PR の安全性は最終的に
// Npgsql が発行する `UPDATE ... WHERE Id=1 AND Version=@original` の実挙動に依存する。InMemory は
// **初回行の同時 INSERT（主キー衝突・23505）に到達できない**（共有ストアのため後発の Find が必ず行を見つける）。
// 実 DB でのみ確かめられるこの経路を含めて固定する（claude-review 指摘への対応）。
//
// [Trait("Category","Integration")]: 既定 CI では --filter Category!=Integration で除外し、
// 専用ワークフロー（integration.yml）で実走する（Docker 必須）。
[Trait("Category", "Integration")]
public sealed class PositionDriftStateConcurrencyE2ETests : IAsyncLifetime
{
    // 外部インフラ注入時（Docker API が無い環境・E2EInfrastructure 参照）はコンテナを起動しない。
    private readonly PostgreSqlContainer? _postgres = E2EInfrastructure.UseExternal
        ? null
        : new PostgreSqlBuilder("postgres:16").Build();

    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            if (_postgres is not null)
            {
                await _postgres.StartAsync();
            }

            _connectionString = E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString();

            // 本番と同じ Migration でスキーマを作る（position_drift_state の DDL 自体も検証対象）。
            await using var db = NewContext();
            await db.Database.MigrateAsync();
        }
        catch
        {
            // IAsyncLifetime は InitializeAsync が例外送出すると DisposeAsync を呼ばない（コンテナリーク防止）。
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private RiskManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    [Fact]
    public async Task 同じ版から同時に保存すると実DBでも片方だけが勝つ()
    {
        await using (var seed = NewContext())
        {
            new EfPositionDriftStateStore(seed).TrySave(new PositionDriftState("SIG", 1, string.Empty, 0))
                .Should().BeTrue();
        }

        // 2 レプリカが同じ版を読んだ状態を、2 つの接続で再現する。
        await using var dbA = NewContext();
        await using var dbB = NewContext();
        var storeA = new EfPositionDriftStateStore(dbA);
        var storeB = new EfPositionDriftStateStore(dbB);
        var readA = storeA.Get();
        var readB = storeB.Get();

        storeA.TrySave(readA with { ConsecutiveCount = 2, ReportedSignature = "SIG" }).Should().BeTrue();
        storeB.TrySave(readB with { ConsecutiveCount = 2, ReportedSignature = "SIG" })
            .Should().BeFalse("Npgsql が発行する WHERE Version=@original が 0 行更新となり負けを検出する");

        await using var verify = NewContext();
        var state = new EfPositionDriftStateStore(verify).Get();
        state.Version.Should().Be(2, "版は 1 回だけ進む（ロストアップデートが起きていない）");
        state.ConsecutiveCount.Should().Be(2);
    }

    [Fact]
    public async Task 初回行の同時挿入は実DBでも片方だけが勝つ()
    {
        // InMemory では到達できない経路（主キー衝突 23505 → DbUpdateException）を実 DB で通す。
        //
        // 単に 2 つのコンテキストから逐次 TrySave しても**この経路には入らない**。後発の TrySave が行う
        // Find が先発のコミット済み行を見つけてしまい、手前の版不一致判定で false になるからである。
        // Task.WhenAll による真の同時実行は、どちらの Find がどちらの INSERT より先かが非決定でありフレークする。
        //
        // そこで **REPEATABLE READ のスナップショット**で「行が無い」という読みを固定する。Postgres は
        // トランザクション最初の文でスナップショットを取るため、先に 1 度読んでおけば以降の Find は
        // 他トランザクションのコミットを見ない。一方で一意制約は実データに対して効くため、INSERT は
        // 確実に 23505 になる——同時挿入と同じ状況を決定的に再現できる。
        await using var dbB = NewContext();
        await using var tx = await dbB.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var storeB = new EfPositionDriftStateStore(dbB);

        // この読みがスナップショットを確定させる（以降 dbB からは「行なし」に見え続ける）。
        storeB.Get().Should().Be(PositionDriftState.Initial);

        // 別接続（＝別レプリカ）が先に行を作ってコミットする。
        await using (var dbA = NewContext())
        {
            new EfPositionDriftStateStore(dbA).TrySave(new PositionDriftState("A", 1, string.Empty, 0))
                .Should().BeTrue();
        }

        // **本テストが主張どおりの経路を通ることの証明**: ここが Initial でなければ TrySave 内部の Find も
        // 行を見つけてしまい、以降の false は「版不一致」による別経路になる（23505 は通らない）。
        storeB.Get().Should().Be(
            PositionDriftState.Initial,
            "スナップショットが他トランザクションのコミットを見ない＝TrySave は INSERT を試みるしかない");

        // dbB は依然「行なし」を読むため INSERT を試み、一意制約違反（23505）になる。
        var act = () => storeB.TrySave(new PositionDriftState("B", 1, string.Empty, 0));

        act.Should().NotThrow("主キー衝突を呼び出し側へ漏らさない").Which.Should().BeFalse();

        await tx.RollbackAsync();

        await using var verify = NewContext();
        new EfPositionDriftStateStore(verify).Get().ObservedSignature
            .Should().Be("A", "勝った側の状態だけが残る");
    }

    [Fact]
    public async Task 古い版での保存は実DBでも何も書かない()
    {
        await using (var seed = NewContext())
        {
            new EfPositionDriftStateStore(seed).TrySave(new PositionDriftState("SIG", 1, string.Empty, 0));
        }

        await using var db = NewContext();
        var store = new EfPositionDriftStateStore(db);

        store.TrySave(new PositionDriftState("STALE", 9, "STALE", 0)).Should().BeFalse();

        var state = store.Get();
        state.ObservedSignature.Should().Be("SIG");
        state.ConsecutiveCount.Should().Be(1);
    }
}
