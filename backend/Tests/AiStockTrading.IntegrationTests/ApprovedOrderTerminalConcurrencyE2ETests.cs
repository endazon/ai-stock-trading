extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using RiskManagementDbContext = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext;
using EfPortfolioLedgerStore = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.EfPortfolioLedgerStore;

namespace AiStockTrading.IntegrationTests;

// 🔴 T-10-590, FR-09, FR-10, UC-06, #847, IADR-0357:
// `approved_orders.TerminalAt` の**並行トークンを実 PostgreSQL で**検証する。
//
// ユニット試験（`EfPortfolioLedgerMarkTerminalConcurrencyTests`）は EF Core InMemory provider で回しているが、
// 🔴 **本 PR のトークンは「元の値が必ず NULL」という特殊なケースである。** EF が発行する述語は
// `WHERE "TerminalAt" = @original` ではなく **`WHERE "TerminalAt" IS NULL`** でなければならず
// （SQL では `NULL = NULL` が真にならないため、素直に等値で組み立てると**常に 0 行更新**になる）、
// **これは InMemory provider では確かめられない**——あちらは SQL を発行しないからである。
//
// つまり InMemory の緑は「意味論としての楽観排他」までしか保証せず、
// **Npgsql が正しい述語を組み立てること**は実 DB でしか固定できない。そこが外れると
// 「常に 0 行更新 → 常に競合例外 → 終端が一度も記録されない」となり、**在庫が永久に解放されない**
// ——#848 の実害の恒久版である。
//
// 先例は `PositionDriftStateConcurrencyE2ETests`（`position_drift_state.Version`）。同じ流儀で置く。
//
// [Trait("Category","Integration")]: 既定 CI では --filter Category!=Integration で除外し、
// 専用ワークフロー（integration.yml）で実走する（Docker 必須）。
[Trait("Category", "Integration")]
public sealed class ApprovedOrderTerminalConcurrencyE2ETests : IAsyncLifetime
{
    // 外部インフラ注入時（Docker API が無い環境・E2EInfrastructure 参照）はコンテナを起動しない。
    private readonly PostgreSqlContainer? _postgres = E2EInfrastructure.UseExternal
        ? null
        : new PostgreSqlBuilder("postgres:16").Build();

    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            if (_postgres is not null)
            {
                await _postgres.StartAsync();
            }

            _connectionString = E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString();

            // 本番と同じ Migration でスキーマを作る（空の移行 AddApprovedOrderTerminalConcurrencyToken も通る）。
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

    public async ValueTask DisposeAsync()
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

    private static OrderIntent CloseIntent(int quantity = 3_381) =>
        new("SOXL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            quantity, 334.09m, PositionEffect.Close);

    private Guid Approve(DateTimeOffset approvedAt)
    {
        var decisionId = Guid.NewGuid();
        using var db = NewContext();
        new EfPortfolioLedgerStore(db).AppendApproval(decisionId, CloseIntent(), approvedAt);
        return decisionId;
    }

    // 🔴 本命: 元の値が NULL の並行トークンが、実 Npgsql で正しい述語（IS NULL）になる。
    //
    // 決定的に再現する: 負ける側に先に行を読み込ませて `TerminalAt = null` を掴ませ（EF は追跡中の
    // エンティティを返すため、以降の Find は他トランザクションのコミットを見ない）、そのあと勝者が書く。
    // Task.WhenAll による真の同時実行はどちらが先かが非決定でフレークするため、ここでは採らない
    // （`PositionDriftStateConcurrencyE2ETests` が同じ理由で REPEATABLE READ を使っているのと同型の配慮）。
    [Fact]
    public async Task 実DBでも終端の初回を主張するのは一度だけ()
    {
        var now = DateTimeOffset.UtcNow;
        var decisionId = Approve(now.AddMinutes(-5));

        await using var loserDb = NewContext();
        await using var winnerDb = NewContext();
        var loser = new EfPortfolioLedgerStore(loserDb);
        var winner = new EfPortfolioLedgerStore(winnerDb);

        // 負ける側が先に行を読む（TerminalAt = null を掴む）。
        loser.FindApprovedIntent(decisionId).Should().NotBeNull();

        winner.MarkTerminal(decisionId, OrderStatus.Cancelled, now).Should().BeTrue();

        loser.MarkTerminal(decisionId, OrderStatus.Expired, now)
            .Should().BeFalse(
                "Npgsql が発行する WHERE \"TerminalAt\" IS NULL が 0 行更新となり負けを検出する"
                    + "（素直な等値述語だと NULL = NULL が偽になり、勝者側まで常に負ける）");
    }

    // 対（肯定形）: **勝者が書けて、在庫の押さえが実際に解ける。**
    //
    // 🔴 これは「上の否定形が見落とす穴を塞ぐため」ではない —— 否定形も
    // `winner.MarkTerminal(...).Should().BeTrue()` を持つので、述語が壊れればあちらも落ちる。
    // 本ケースが独立して価値を持つのは、**固定している性質が違う**からである。あちらは
    // 「2 つの文脈が競ったとき勝者は 1 つ」、こちらは「**終端が在庫の押さえを実際に解く**」
    // ——`GetInFlightCloseQuantity` の集計（`TerminalAt IS NULL` の絞り込み）まで含めて
    // 実 DB で通す唯一のケースであり、#847 の利用者に見える結末そのものである。
    [Fact]
    public async Task 実DBで終端が記録され在庫の押さえが解ける()
    {
        var now = DateTimeOffset.UtcNow;
        var decisionId = Approve(now.AddMinutes(-5));

        await using (var db = NewContext())
        {
            new EfPortfolioLedgerStore(db)
                .GetInFlightCloseQuantity("SOXL", Market.UnitedStates, now.AddMinutes(-30))
                .Should().Be(3_381, "終端の前は全量が処理中である");
        }

        await using (var db = NewContext())
        {
            new EfPortfolioLedgerStore(db).MarkTerminal(decisionId, OrderStatus.Cancelled, now)
                .Should().BeTrue();
        }

        await using var verify = NewContext();
        new EfPortfolioLedgerStore(verify)
            .GetInFlightCloseQuantity("SOXL", Market.UnitedStates, now.AddMinutes(-30))
            .Should().Be(0, "確認できた終端は在庫の押さえを解く");
    }

    // 否定形: 単調である（後着の終端で時刻も状態も動かない）。実 DB でも同じ。
    [Fact]
    public async Task 実DBでも終端は単調で後着では動かない()
    {
        var now = DateTimeOffset.UtcNow;
        var decisionId = Approve(now.AddMinutes(-5));

        await using (var db = NewContext())
        {
            new EfPortfolioLedgerStore(db).MarkTerminal(decisionId, OrderStatus.Cancelled, now.AddMinutes(-2))
                .Should().BeTrue();
        }

        await using (var db = NewContext())
        {
            new EfPortfolioLedgerStore(db).MarkTerminal(decisionId, OrderStatus.Expired, now)
                .Should().BeFalse("最初の終端が真（後着は書かない）");
        }

        await using var verify = NewContext();
        new EfPortfolioLedgerStore(verify)
            .GetInFlightCloseQuantity("SOXL", Market.UnitedStates, now.AddMinutes(-30))
            .Should().Be(0);
    }
}
