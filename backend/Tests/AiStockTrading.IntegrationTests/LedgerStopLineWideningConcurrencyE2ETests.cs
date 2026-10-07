extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using RiskManagementDbContext = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext;
using EfPortfolioLedgerStore = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.EfPortfolioLedgerStore;

namespace AiStockTrading.IntegrationTests;

// 🔴 T-10-1934, FR-10, ADR-0049, #1136, IADR-0472 決定6:
// 取引台帳の損切りラインの追随（`WidenStopLoss`）が、**実 PostgreSQL で**並行しても狭い値で上書きしないこと。
//
// 関係 DB では判定と書き込みを 1 文の条件付き UPDATE（ExecuteUpdate）にまとめている。InMemory provider は
// ExecuteUpdate を持たず SQL も発行しないので、**この経路は実 DB でしか固定できない**
// （先例は `ApprovedOrderTerminalConcurrencyE2ETests`。同じ流儀で置く）。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する（実 PostgreSQL 等の依存を得られなければ理由つきで skip。門は RequiredServices・IADR-0497）。
[Trait("Category", "Integration")]
public sealed class LedgerStopLineWideningConcurrencyE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public LedgerStopLineWideningConcurrencyE2ETests()
    {
        // NFR, MSP/ADR-0090 決定 1・2, IADR-0497 (#1200): 要る依存を得られなければ、コンテナを組み立てる前に理由つきで skip する
        // （Docker に届かない環境では `Build()` 自体が投げるため、門はフィールド初期化子より前＝ここに置く）。
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);

        // 外部インフラ注入時（E2E_*。E2EInfrastructure 参照）はコンテナを起動しない。
        _postgres = E2EInfrastructure.UseExternal ? null : new PostgreSqlBuilder("postgres:16").Build();
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            if (_postgres is not null)
            {
                await _postgres.StartAsync();
            }

            _connectionString = E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString();

            await using var db = NewContext();
            await db.Database.MigrateAsync();
        }
        catch
        {
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

    private Guid ApproveOpen(TradeSide side, decimal? stopLoss, PositionEffect effect = PositionEffect.Open)
    {
        var decisionId = Guid.NewGuid();
        using var db = NewContext();
        new EfPortfolioLedgerStore(db).AppendApproval(
            decisionId,
            new OrderIntent("NVDA", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate,
                10, 230.82m, effect, stopLoss),
            DateTimeOffset.UtcNow.AddMinutes(-5));
        return decisionId;
    }

    private decimal? StopLossOf(Guid decisionId)
    {
        using var db = NewContext();
        return db.ApprovedOrders.AsNoTracking().Single(a => a.DecisionId == decisionId).StopLossPrice;
    }

    // 🔴 本命: 負ける側が先に行を読み（旧ラインを掴む）、勝者がより広く書いたあとで、負ける側が
    // 「旧ラインより広いが勝者より狭い」値を書こうとしても、狭い値で上書きしない（最も広いラインに収束する）。
    // 読んでから書く実装だと、負ける側は掴んだ旧ラインと比べて「広げる向き」と判定し、勝者の値を上書きする。
    [Fact]
    public void 実DBでは並行する追随が狭い値で上書きしない_買い建て()
    {
        var decisionId = ApproveOpen(TradeSide.Buy, 226.52m);

        using var loserDb = NewContext();
        using var winnerDb = NewContext();
        var loser = new EfPortfolioLedgerStore(loserDb);
        var winner = new EfPortfolioLedgerStore(winnerDb);

        // 負ける側が先に行を読む（追跡中のエンティティが旧ライン 226.52 を持つ）。
        loserDb.ApprovedOrders.Find(decisionId)!.StopLossPrice.Should().Be(226.52m);

        winner.WidenStopLoss(decisionId, TradeSide.Buy, 225.00m).Should().BeTrue();
        loser.WidenStopLoss(decisionId, TradeSide.Buy, 226.20m)
            .Should().BeFalse("DB の今のライン 225.00 より狭いので、条件付き UPDATE は 0 行になる");

        StopLossOf(decisionId).Should().Be(225.00m);
    }

    // 売り建ては向きが逆（高いほど広い）。
    [Fact]
    public void 実DBでは並行する追随が狭い値で上書きしない_売り建て()
    {
        var decisionId = ApproveOpen(TradeSide.Sell, 235.00m);

        using var loserDb = NewContext();
        using var winnerDb = NewContext();
        loserDb.ApprovedOrders.Find(decisionId)!.StopLossPrice.Should().Be(235.00m);

        new EfPortfolioLedgerStore(winnerDb).WidenStopLoss(decisionId, TradeSide.Sell, 236.00m).Should().BeTrue();
        new EfPortfolioLedgerStore(loserDb).WidenStopLoss(decisionId, TradeSide.Sell, 235.44m).Should().BeFalse();

        StopLossOf(decisionId).Should().Be(236.00m);
    }

    // 否定形: 狭める向き・ライン未記録・向き違い・決済の行・知らない判断は書かない（実 DB の述語でも同じ）。
    [Fact]
    public void 実DBでも狭める向きと対象外は書かない()
    {
        var buy = ApproveOpen(TradeSide.Buy, 226.52m);
        var noLine = ApproveOpen(TradeSide.Buy, null);
        var close = ApproveOpen(TradeSide.Sell, 226.52m, PositionEffect.Close);

        using var db = NewContext();
        var store = new EfPortfolioLedgerStore(db);

        store.WidenStopLoss(buy, TradeSide.Buy, 227.00m).Should().BeFalse("買い建てで高くするのは狭める向き");
        store.WidenStopLoss(buy, TradeSide.Buy, 226.52m).Should().BeFalse("同じ値は書かない（冪等）");
        // #1136 独立監査 F5: 売りの向きの述語（StopLossPrice < 230）には一致するので、止めるのは WHERE の方向（Side）だけ。
        store.WidenStopLoss(buy, TradeSide.Sell, 230.00m).Should().BeFalse("向きが違う");
        store.WidenStopLoss(noLine, TradeSide.Buy, 220.00m).Should().BeFalse("未記録のラインは埋めない");
        store.WidenStopLoss(close, TradeSide.Sell, 230.00m).Should().BeFalse("決済の行は触らない");
        store.WidenStopLoss(Guid.NewGuid(), TradeSide.Buy, 220.00m).Should().BeFalse("知らない判断");

        StopLossOf(buy).Should().Be(226.52m);
        StopLossOf(noLine).Should().BeNull();
        StopLossOf(close).Should().Be(226.52m);
    }
}
