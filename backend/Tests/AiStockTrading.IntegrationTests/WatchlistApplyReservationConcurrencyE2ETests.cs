extern alias ReportWorker;

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using EfPolicyRevisionLedger = ReportWorker::ReportService.Infrastructure.Persistence.EfPolicyRevisionLedger;
using PolicyRevisionAttempt = ReportWorker::ReportService.Features.Reports.PolicyRevisionAttempt;
using PolicyRevisionAttemptOutcome = ReportWorker::ReportService.Features.Reports.PolicyRevisionAttemptOutcome;
using ReportDbContext = ReportWorker::ReportService.Infrastructure.Persistence.ReportDbContext;

namespace AiStockTrading.IntegrationTests;

// 🔴 NFR-06, FR-13, ADR-0042, IADR-0634: 入れ替えの適用の予約（ReserveWatchlistApply）が、**実 PostgreSQL で**並行しても 1 件だけを通すこと。
// 予約の時刻（WatchlistApplyReservedAt）は同時実行のトークンで、UPDATE は「まだ予約が無い」行だけを更新する。
// 同じ案へ 2 つの予約を同時に流すと、成功は 1 件・もう 1 件は false（市場監視では 403＝二重適用を止める）になる。
// InMemory provider の試験（PolicyRevisionLedgerTests）は UPDATE の WHERE を実 DB と同じには評価しないので、ここで実 DB の判定を固定する。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する（門は RequiredServices・IADR-0497）。
[Trait("Category", "Integration")]
public sealed class WatchlistApplyReservationConcurrencyE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public WatchlistApplyReservationConcurrencyE2ETests()
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

    private ReportDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    // 確定前の段まで進んだ案の行を作る（予約の判定は「予約・記録が無い行」だけを見るので、確定の有無は関わらない）。
    private Guid SeedProposed(int round)
    {
        using var db = NewContext();
        var ledger = new EfPolicyRevisionLedger(db);
        var attempt = new PolicyRevisionAttempt(
            Guid.NewGuid(), DateTimeOffset.UtcNow, new DateOnly(2026, 11, 7), "owner", $"daily-2026-11-07-r{round}");
        ledger.TryBegin(attempt, 1000).Begun.Should().BeTrue();
        ledger.Complete(attempt.Id, PolicyRevisionAttemptOutcome.Proposed, 2, "[]");
        return attempt.Id;
    }

    [Fact]
    public async Task 同じ案への2つの同時の予約は1件だけが通る()
    {
        // 交錯の形は実行ごとに違い得るので、何度か流して毎回 1 件だけであることを確かめる。
        for (var round = 0; round < 10; round++)
        {
            var id = SeedProposed(round);
            using var gate = new Barrier(2);

            bool Reserve(DateTimeOffset at)
            {
                using var db = NewContext();
                var ledger = new EfPolicyRevisionLedger(db);
                // 両方の書き手が「まだ予約が無い」と読んでから書くように、読みを揃えてから同時に流す。
                db.PolicyRevisionAttempts.Find(id)!.WatchlistApplyReservedAt.Should().BeNull();
                gate.SignalAndWait(TimeSpan.FromSeconds(30));
                return ledger.ReserveWatchlistApply(id, at);
            }

            var first = DateTimeOffset.UtcNow;
            var second = first.AddSeconds(1);
            var results = await Task.WhenAll(
                Task.Run(() => Reserve(first), TestContext.Current.CancellationToken),
                Task.Run(() => Reserve(second), TestContext.Current.CancellationToken));

            results.Count(r => r).Should().Be(1, $"round {round}: 同じ案の予約は 1 件だけが通る（他方は false＝二重適用を止める）");
            await using var check = NewContext();
            var reservedAt = (await check.PolicyRevisionAttempts.SingleAsync(a => a.Id == id, TestContext.Current.CancellationToken))
                .WatchlistApplyReservedAt;
            reservedAt.Should().NotBeNull();
            // 勝った側の時刻が残る（負けた側が上書きしていない）。Postgres の timestamptz はマイクロ秒まで。
            var winner = results[0] ? first : second;
            reservedAt!.Value.Should().BeCloseTo(winner, TimeSpan.FromMilliseconds(1), $"round {round}: 負けた側は予約を上書きしない");

            await using var again = NewContext();
            new EfPolicyRevisionLedger(again).ReserveWatchlistApply(id, second.AddSeconds(1))
                .Should().BeFalse($"round {round}: 予約済みの案はもう予約できない");
        }
    }
}
