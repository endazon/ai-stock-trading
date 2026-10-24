using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 FR-10, ADR-0067 決定1・2, #1473, IADR-0587:
// 判断由来の決済の未約定取消（#1339）の照会へ、**UTC 以外のオフセットの DateTimeOffset**（米国東部の取引日の始まり＝−04:00、
// 日本株の取引日＝+09:00）を渡しても、**実 PostgreSQL で照会・書き込みが例外にならない**ことを固定する。
//
// 稼働（2026-10-24）では Npgsql が `Cannot write DateTimeOffset with Offset=-04:00:00 to PostgreSQL type
// 'timestamp with time zone', only offset 0 (UTC) is supported.` を投げ、取消の巡回が 30 秒ごとに失敗して一度も取り消さなかった。
// 🔴 **単体試験（InMemory の store・UTC の時計）では再現しない**——InMemory は SQL のパラメータを書かないため、
// この型の誤りは実 DB でしか捕まらない（ApprovedOrderTerminalConcurrencyE2ETests と同じ理由で実 DB に置く）。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、integration.yml で実走する（依存を得られなければ理由つきで skip。IADR-0497）。
[Trait("Category", "Integration")]
public sealed class ExecutedOrderStoreNonUtcOffsetE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan Eastern = TimeSpan.FromHours(-4);
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public ExecutedOrderStoreNonUtcOffsetE2ETests()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
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

            // 本番と同じ Migration でスキーマを作る（変換の規約がモデルの差分＝未適用の移行を生まないことも、ここで確かめる）。
            await using var db = NewContext();
            await db.Database.MigrateAsync();
            // 外部インフラ（E2E_*）は状態が残るので、試験の行だけを毎回消す。
            await db.ExecutedOrders.Where(r => r.Symbol.StartsWith("T1473")).ExecuteDeleteAsync();
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

    private OrderExecutionDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private static ExecutionRecord DecisionClose(string symbol, DateTimeOffset placedAt, Market market = Market.UnitedStates) =>
        new(Guid.NewGuid(), $"{symbol}-{Guid.NewGuid():N}", symbol, market, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 100, 25.10m, 0, 0m, OrderStatus.Accepted, 0m, placedAt,
            ApprovalOrigin: OrderApprovalOrigin.TradeDecision);

    private void Save(ExecutionRecord record)
    {
        using var db = NewContext();
        new EfExecutedOrderStore(db).Save(record);
    }

    // 🔴 T-10-3068 本命（稼働の症状そのもの）: 照会の上限・下限が −04:00（米国東部の取引日の始まり）でも照会が通り、対象を返す。
    [Fact]
    public void 米国東部のオフセットの境界で判断由来の決済の未約定を照会できる()
    {
        var now = new DateTimeOffset(2026, 10, 26, 14, 30, 0, TimeSpan.Zero);
        var placed = DecisionClose("T1473A", now.AddMinutes(-20));
        Save(placed);

        var dayStartEastern = new DateTimeOffset(2026, 10, 26, 0, 0, 0, Eastern);
        var placedAtOrBeforeEastern = now.AddMinutes(-15).ToOffset(Eastern);

        using var db = NewContext();
        var found = new EfExecutedOrderStore(db)
            .FindUnfilledDecisionCloses(placedAtOrBeforeEastern, 50, placedOnOrAfter: dayStartEastern);

        found.Select(r => r.OrderId).Should().Contain(placed.OrderId,
            "照会の境界のオフセットは瞬間を変えない（−04:00 のまま Npgsql へ渡すと例外になり、取消が一度も行われない）");
    }

    // T-10-3068 対: 日本株の取引日（+09:00）の境界でも同じ（市場ごとの取引日は JST でも作る）。
    [Fact]
    public void 日本時間のオフセットの境界で判断由来の決済の未約定を照会できる()
    {
        var now = new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero);
        var placed = DecisionClose("T1473B", now.AddMinutes(-30), Market.Japan);
        Save(placed);

        var dayStartJst = new DateTimeOffset(2026, 10, 26, 0, 0, 0, Jst);

        using var db = NewContext();
        var found = new EfExecutedOrderStore(db)
            .FindUnfilledDecisionCloses(now.AddMinutes(-15).ToOffset(Jst), 50, placedOnOrAfter: dayStartJst);

        found.Select(r => r.OrderId).Should().Contain(placed.OrderId);
    }

    // 🔴 T-10-3069 照会が通った次の段（回数の上限）: null を許す列（UnfilledCancelRequestedAt）を −04:00 の [始まり, 終わり) で数える。
    // 稼働では最初の照会で落ちたので未到達だったが、呼び出し側は同じ形の境界を渡す。
    [Fact]
    public void 米国東部のオフセットの取引日で取消の回数を数えられる()
    {
        var now = new DateTimeOffset(2026, 10, 26, 14, 30, 0, TimeSpan.Zero);
        var placed = DecisionClose("T1473C", now.AddMinutes(-20));
        Save(placed);

        using (var db = NewContext())
        {
            // 書き込みの側にも東部のオフセットを渡す（境界で UTC へ揃うこと）。
            new EfExecutedOrderStore(db).MarkUnfilledCancelRequested(placed.OrderId, now.ToOffset(Eastern))
                .Should().BeTrue();
        }

        using (var db = NewContext())
        {
            new EfExecutedOrderStore(db).CountUnfilledCancelRequests(
                    "T1473C", Market.UnitedStates, TradeSide.Sell,
                    new DateTimeOffset(2026, 10, 26, 0, 0, 0, Eastern),
                    new DateTimeOffset(2026, 10, 27, 0, 0, 0, Eastern))
                .Should().Be(1);
        }
    }

    // T-10-3070 書き込み（INSERT）の側: 記録の時刻が UTC 以外のオフセットでも保存でき、同じ瞬間（UTC）で読み戻せる。
    [Fact]
    public void UTC以外のオフセットの発注時刻を保存して同じ瞬間で読み戻せる()
    {
        var placedJst = new DateTimeOffset(2026, 10, 26, 9, 5, 0, Jst);
        var placed = DecisionClose("T1473D", placedJst, Market.Japan);
        Save(placed);

        using var db = NewContext();
        var read = new EfExecutedOrderStore(db).FindByDecisionId(placed.DecisionId);

        read.Should().NotBeNull();
        read!.ExecutedAt.Should().Be(placedJst);
        read.ExecutedAt.Offset.Should().Be(TimeSpan.Zero, "timestamptz は UTC で読み戻る");
    }
}
