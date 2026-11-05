extern alias RiskManagementWorker;

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using EfKillSwitchStore = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.EfKillSwitchStore;
using KillSwitchState = RiskManagementWorker::RiskManagementService.Features.RiskManagement.KillSwitchState;
using RiskManagementDbContext = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext;

namespace AiStockTrading.IntegrationTests;

// 🔴 FR-10, FR-14, #1534, IADR-0620: kill switch の版の前提条件が、**実 PostgreSQL で**並行しても 1 件だけを通すこと。
// 比較と書き込みは直列化可能な取引の中で行い、版の列は並行トークンである。同じ版を前提にした 2 件の書き込みを同時に流すと、
// 交錯の仕方（後発が新しい版を読む／直列化の失敗 40001／楽観排他の失敗）に関わらず、成功は 1 件・もう 1 件は false（REST では 409）になる。
// InMemory provider は取引も直列化も持たないので、この経路は実 DB でしか固定できない。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する（門は RequiredServices・IADR-0497）。
[Trait("Category", "Integration")]
public sealed class KillSwitchVersionConcurrencyE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public KillSwitchVersionConcurrencyE2ETests()
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

    private RiskManagementDbContext NewContext() =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseNpgsql(_connectionString)
            .Options);

    private string CurrentVersion()
    {
        using var db = NewContext();
        return new EfKillSwitchStore(db).GetState().Version;
    }

    [Fact]
    public async Task 同じ版を前提にした2件の同時の書き込みは1件だけが通る()
    {
        using (var seed = NewContext())
            new EfKillSwitchStore(seed).SetState(new KillSwitchState(true, "owner", "急変", DateTimeOffset.UtcNow));

        // 交錯の形は実行ごとに違い得るので、何度か流して毎回 1 件だけであることを確かめる。
        for (var round = 0; round < 10; round++)
        {
            var seen = CurrentVersion();
            using var gate = new Barrier(2);

            bool Write(bool engaged)
            {
                using var db = NewContext();
                var store = new EfKillSwitchStore(db);
                gate.SignalAndWait(TimeSpan.FromSeconds(30));
                return store.TrySetState(new KillSwitchState(engaged, "owner", $"round {round}", DateTimeOffset.UtcNow), seen);
            }

            var results = await Task.WhenAll(
                Task.Run(() => Write(false), TestContext.Current.CancellationToken),
                Task.Run(() => Write(true), TestContext.Current.CancellationToken));

            results.Count(r => r).Should().Be(1, $"round {round}: 同じ版（{seen}）を前提にした書き込みは 1 件だけが通る");
            CurrentVersion().Should().Be((long.Parse(seen, System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture), "版は 1 つだけ進む");
        }
    }
}
