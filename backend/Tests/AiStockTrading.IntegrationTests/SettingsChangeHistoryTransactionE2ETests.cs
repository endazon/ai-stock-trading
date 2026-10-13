extern alias RiskManagementWorker;

using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;
using EfRiskSettingsStore = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.EfRiskSettingsStore;
using EfSettingsChangeLog = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.EfSettingsChangeLog;
using EfSettingsWriteTransaction = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.EfSettingsWriteTransaction;
using HighVolatilitySettings = RiskManagementWorker::RiskManagementService.Domain.HighVolatilitySettings;
using HighVolatilitySymbol = RiskManagementWorker::RiskManagementService.Domain.HighVolatilitySymbol;
using IBrokerAccountObservationStore = RiskManagementWorker::RiskManagementService.Features.RiskManagement.IBrokerAccountObservationStore;
using ISettingsChangeLog = RiskManagementWorker::RiskManagementService.Features.RiskManagement.ISettingsChangeLog;
using RiskManagementDbContext = RiskManagementWorker::RiskManagementService.Infrastructure.Persistence.RiskManagementDbContext;
using RiskSettingsService = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskSettingsService;
using SettingsChangeEntry = RiskManagementWorker::RiskManagementService.Features.RiskManagement.SettingsChangeEntry;
using SettingsChangeType = RiskManagementWorker::RiskManagementService.Features.RiskManagement.SettingsChangeType;
using SystemClock = RiskManagementWorker::RiskManagementService.Common.Abstractions.SystemClock;

namespace AiStockTrading.IntegrationTests;

// 🔴 FR-11, FR-10, UC-06, ADR-0063 決定2, ADR-0007, #1291, IADR-0531:
// 高ボラティリティ銘柄の設定変更が、**実 PostgreSQL で**変更履歴に 1 行書かれ（列は名前・序数は 10）、
// 履歴の書き込みが失敗したら設定の変更も取り消されること（設定の保存と履歴の記録が同じトランザクション）。
//
// 単体試験の WebApplicationFactory は EF InMemory で、InMemory はトランザクションを持たない（EfSettingsWriteTransaction は
// 本体をそのまま走らせる）。**ロールバックは実 DB でしか固定できない**（先例 `LedgerStopLineWideningConcurrencyE2ETests` と同じ流儀）。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する（依存を得られなければ理由つきで skip。門は RequiredServices・IADR-0497）。
[Trait("Category", "Integration")]
public sealed class SettingsChangeHistoryTransactionE2ETests : IAsyncLifetime
{
    private const string Reason = "PoC 2026-10-09 TSLA incident remediation (AST#1291)";

    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public SettingsChangeHistoryTransactionE2ETests()
    {
        // NFR, MSP/ADR-0090 決定 1・2, IADR-0497 (#1200): 要る依存を得られなければ、コンテナを組み立てる前に理由つきで skip する。
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

    // 本番の配線と同じ形: 設定のストア・履歴・トランザクションが 1 つの DbContext（scoped）を共有する。
    private static RiskSettingsService NewService(RiskManagementDbContext db, ISettingsChangeLog? changeLog = null) =>
        new(new EfRiskSettingsStore(db), changeLog ?? new EfSettingsChangeLog(db), new SystemClock(),
            new NoAccountObservation(), new EfSettingsWriteTransaction(db));

    private static HighVolatilitySettings TslaDesignated() => new()
    {
        MaxOrderAmountRatio = 0.05m,
        DesignatedSymbols = [new HighVolatilitySymbol("TSLA", Market.UnitedStates)],
    };

    private int HistoryCount()
    {
        using var db = NewContext();
        return db.SettingsChangeLog.AsNoTracking().Count();
    }

    // T-10-2614: 実 DB で、高ボラの変更は履歴にちょうど 1 行書かれる。列は名前 `HighVolatilityChanged`、照会では序数 10 の種別へ戻る。
    [Fact]
    public void T_10_2614_実DBで高ボラの変更は履歴に1行書かれ種別は10()
    {
        using (var seed = NewContext())
        {
            NewService(seed).GetCurrent(); // 既定値のシード（履歴は書かない）
        }

        var before = HistoryCount();

        using (var db = NewContext())
        {
            NewService(db).UpdateHighVolatility(TslaDesignated(), "owner", Reason);
        }

        HistoryCount().Should().Be(before + 1);
        using var read = NewContext();
        var row = read.SettingsChangeLog.AsNoTracking().OrderByDescending(r => r.ChangedAt).First();
        row.ChangeType.Should().Be("HighVolatilityChanged");
        row.Reason.Should().Be(Reason);
        row.After.Should().Contain("TSLA(UnitedStates)");

        var entry = new EfSettingsChangeLog(read).GetHistory()[0];
        entry.ChangeType.Should().Be(SettingsChangeType.HighVolatilityChanged);
        ((int)entry.ChangeType).Should().Be(10);
        NewService(read).GetCurrent().HighVolatility.Should().Be(TslaDesignated());
    }

    // 🔴 T-10-2615: 実 DB で、履歴の書き込みが失敗したら設定の変更も取り消される（「設定は変わったが履歴が無い」を残さない）。
    // 旧実装（設定と履歴が別々の SaveChanges＝別トランザクション）では、設定だけが TSLA を指定した状態で残る。
    [Fact]
    public void T_10_2615_実DBで履歴の書き込みが失敗したら設定も変わらない()
    {
        HighVolatilitySettings original;
        using (var seed = NewContext())
        {
            original = NewService(seed).GetCurrent().HighVolatility;
        }

        // 外部供給の DB（E2E_*）では他の試験の書き込みが残り得るため、現在値と必ず異なる値を作って変更する。
        var target = new HighVolatilitySettings
        {
            MaxOrderAmountRatio = original.MaxOrderAmountRatio == 0.04m ? 0.03m : 0.04m,
            DesignatedSymbols = [new HighVolatilitySymbol("T2615", Market.UnitedStates)],
        };
        target.Should().NotBe(original, "前提: 変更として観測できる値である");
        var before = HistoryCount();

        using (var db = NewContext())
        {
            var failing = new FailingAfterRecordChangeLog(new EfSettingsChangeLog(db));
            var act = () => NewService(db, failing).UpdateHighVolatility(target, "owner", Reason);
            act.Should().Throw<InvalidOperationException>().WithMessage("*履歴の書き込みの失敗を模す*");
        }

        using var read = NewContext();
        NewService(read).GetCurrent().HighVolatility.Should().Be(original, "履歴が書けなければ設定も変えない");
        HistoryCount().Should().Be(before, "履歴の行も取り消される");
    }

    // 履歴の行を DB へ書いた（SaveChanges まで済んだ）後で失敗する。トランザクションが無ければ設定も履歴も残る形。
    private sealed class FailingAfterRecordChangeLog(ISettingsChangeLog inner) : ISettingsChangeLog
    {
        public void Record(SettingsChangeEntry entry)
        {
            inner.Record(entry);
            throw new InvalidOperationException("履歴の書き込みの失敗を模す（T-10-2615）");
        }

        public IReadOnlyList<SettingsChangeEntry> GetHistory() => inner.GetHistory();
    }

    // 口座種別の観測なし（設定側の制限は掛からない。本試験の関心外）。
    private sealed class NoAccountObservation : IBrokerAccountObservationStore
    {
        public void Record(BrokerAccountState account, DateTimeOffset observedAt)
        {
        }

        public BrokerAccountState? GetCurrent() => null;
    }
}
