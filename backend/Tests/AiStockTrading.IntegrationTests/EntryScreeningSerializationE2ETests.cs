extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Common.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using RiskManagementWorker::RiskManagementService.Features.RiskManagement;
using RiskManagementWorker::RiskManagementService.Infrastructure.ExternalServices;
using RiskManagementWorker::RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 T-10-2579, FR-10, FR-20, ADR-0016 決定9, #1319, IADR-0529 決定1・2:
// 新規建ての審査の直列化（`pg_advisory_xact_lock`）と予約を**実 PostgreSQL で**検証する。
//
// ユニット試験（`EntryScreeningSerializationTests`）は InMemory のストアで回しており、直列化はプロセス内のセマフォである。
// 🔴 **本番の直列化は DB の advisory lock とトランザクションであり、InMemory では確かめられない**——
// 「ロックを取った後の読み取りが、前の審査がコミットした予約を見る」（READ COMMITTED の文ごとのスナップショット）も、
// 「例外で抜けたらロールバックして予約が残らない」も、実 DB でしか固定できない。
//
// 審査ごとに**別の DbContext（別の接続）**を作る＝本番の Wolverine の scope ごとの生成と同じ。読み取りの後に待ちを入れて
// 並行な審査どうしの窓を広げる（直列化が無ければ全件が空の snapshot を読んで承認する）。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する。
[Trait("Category", "Integration")]
public sealed class EntryScreeningSerializationE2ETests : IAsyncLifetime
{
    private const decimal Equity = 100_000m;

    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public EntryScreeningSerializationE2ETests()
    {
        // NFR, MSP/ADR-0090 決定 1・2, IADR-0497 (#1200): 要る依存を得られなければ理由つきで skip する。
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

            // 本番と同じ Migration でスキーマを作る（AddEntryReservations を含む）。
            await using var db = NewContext();
            await db.Database.MigrateAsync();
            // 外部インフラ注入時は DB を使い回すため、前回の行を消して独立させる。
            await db.EntryReservations.ExecuteDeleteAsync();
            await db.ApprovedOrders.ExecuteDeleteAsync();
            await db.OrderActivities.ExecuteDeleteAsync();
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

    private sealed class SlowStateProvider(IPortfolioStateProvider inner, TimeSpan delay) : IPortfolioStateProvider
    {
        public PortfolioState GetCurrent()
        {
            var state = inner.GetCurrent();
            Thread.Sleep(delay);
            return state;
        }
    }

    private sealed class AffirmedInformation : IInformationDegradationStore
    {
        public bool BlocksNewEntries => false;

        public void MarkDegraded(string category)
        {
        }

        public void MarkRecovered(string category)
        {
        }

        public void ApplyObservation(
            IReadOnlyCollection<string> blockingCategories, TimeSpan validFor, DateTimeOffset observedAt)
        {
        }
    }

    private sealed class ObservedCapital : ICapitalBaselineStore
    {
        public void Record(decimal equityInBase, DateTimeOffset observedAt)
        {
        }

        public CapitalBaseline? GetCurrent() =>
            new(Equity, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), DateTimeOffset.UtcNow.AddHours(-1));

        // #1423 M-3: 表示・通知の口（本試験は使わない）。
        public CapitalBaselineReading Read() =>
            new(GetCurrent(), AiStockTrading.Shared.Contracts.Observability.CapitalBaselineReadOutcome.Supplied,
                DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), Equity);
    }

    private sealed class NoPrices : ICurrentPriceSource
    {
        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(
            IReadOnlyList<OpenPosition> positions) => new Dictionary<(string Symbol, Market Market), decimal>();
    }

    // #1396, IADR-0561: 実時刻が米国の閉場前の新規建ての締め切り（閉場の 60 分前以降）に入っているときは、同じ市場ローカルの日のうち
    // 締め切りの 1 分前へ戻す（本試験は直列化を確かめるもので、締め切りの拒否を混ぜない。取引日は変わらない）。
    // NFR, #1429: TradeExecutionPipelineE2ETests のリスク管理ホストも同じ時計を使う（internal はそのため）。
    internal sealed class OutsidePreCloseCutoffClock : IClock
    {
        public DateTimeOffset UtcNow
        {
            get
            {
                var now = DateTimeOffset.UtcNow;
                var close = AiStockTrading.Shared.Kernel.Trading.MarketHours.SessionCloseOn(Market.UnitedStates, now);
                return AiStockTrading.Shared.Kernel.Trading.PreCloseWindow.IsAtOrAfter(
                        now, close, TradingDefaults.PreCloseEntryCutoffMinutes)
                    ? close!.Value.AddMinutes(-TradingDefaults.PreCloseEntryCutoffMinutes - 1).ToUniversalTime()
                    : now;
            }
        }

        public DateOnly Today => TradingDay.Of(UtcNow);
    }

    // 本番と同じ EF のストア（台帳・注文源・予約）を 1 つの DbContext に載せ、残りの状態はインメモリで「健全」にする。
    private static OrderScreeningService Screening(RiskManagementDbContext db, RiskManagementSettings settings)
    {
        var clock = new OutsidePreCloseCutoffClock();
        var ledger = new EfPortfolioLedgerStore(db);
        var working = new EfWorkingEntryOrderSource(db);
        var provider = new SlowStateProvider(
            new LedgerPortfolioStateProvider(ledger, working, clock), TimeSpan.FromMilliseconds(50));
        var snapshots = new PortfolioSnapshotBuilder(
            provider, new InMemoryKillSwitchStore(), new InMemoryPauseStore(),
            new InMemoryBrokerAccountObservationStore(TimeProvider.System), new AffirmedInformation(),
            capitalBaseline: new ObservedCapital());
        var shortSell = new ShortSellContextSupplier(
            new UnavailableShortSellBorrowSource(), ledger, working,
            new NoPrices(), new UnavailableMaintenanceMarginSnapshotSource(), clock,
            NullLogger<ShortSellContextSupplier>.Instance);
        return new OrderScreeningService(
            new InMemoryRiskSettingsStore(settings), snapshots, new InMemoryLockoutStore(), clock,
            new WeekendBusinessCalendar(), new InMemoryBuyInInferenceStore(), ledger, shortSell,
            new EfEntryReservationStore(db), ConfiguredSessionAndThemeControls.Default);
    }

    private static TradeDecisionMade Decision(string symbol, int quantity = 10, decimal price = 10m) =>
        new(Guid.NewGuid(),
            new OrderIntent(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, price),
            "定時判断", DateTimeOffset.UtcNow);

    private static readonly string[] Symbols = ["AAPL", "MSFT", "NVDA", "AMZN", "META", "GOOG"];

    private async Task<ScreeningOutcome[]> ScreenConcurrentlyAsync(
        RiskManagementSettings settings, Func<string, TradeDecisionMade> decision)
    {
        using var start = new ManualResetEventSlim(false);
        // 専用スレッドで同時に走らせる（スレッドプールの注入待ちで審査が自然に順番になる形を避ける）。
        var tasks = Symbols
            .Select(symbol => Task.Factory.StartNew(
                () =>
                {
                    using var db = NewContext();
                    var screening = Screening(db, settings);
                    start.Wait();
                    return screening.ScreenAsync(decision(symbol)).GetAwaiter().GetResult();
                },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToList();
        start.Set();
        return await Task.WhenAll(tasks);
    }

    // 🔴 本命（PoC 2026-10-12 の形）: 保有建玉数の上限 3（既定）・建玉 0 で、6 銘柄の新規買いが同時に届く。
    // 承認は 3 件まで、残りは MaxPositionsExceeded。予約は承認した 3 件だけが DB に残る。
    [Fact]
    public async Task 実DBで並行に届いた新規建ては保有建玉数の上限を超える分が拒否される()
    {
        var outcomes = await ScreenConcurrentlyAsync(TradingDefaults.CreateSettings(), s => Decision(s));

        outcomes.Count(o => o.IsApproved).Should().Be(3, "上限 3 まで（是正前は 6 件とも承認された）");
        outcomes.Where(o => !o.IsApproved).Should().AllSatisfy(
            o => o.Rejected!.Reasons.Should().Contain(RejectionReason.MaxPositionsExceeded));

        await using var verify = NewContext();
        (await verify.EntryReservations.CountAsync()).Should().Be(3, "予約は承認した分だけ・コミット済み");
        (await verify.ApprovedOrders.CountAsync()).Should().Be(0, "前提: 承認行は書かれていない（OrderApproved の往復の前）");
    }

    // 日次発注累計も同じ（equity $100,000 × 50% ＝ $50,000。1 件 $20,000 → 2 件まで）。
    [Fact]
    public async Task 実DBで並行に届いた新規建ては日次発注累計の上限を超える分が拒否される()
    {
        var defaults = TradingDefaults.CreateSettings();
        var settings = defaults with
        {
            Limits = defaults.Limits with { MaxOpenPositions = 10, MaxDailyOrderAmountRatio = 0.5m },
        };

        var outcomes = await ScreenConcurrentlyAsync(settings, s => Decision(s, 200, 100m));

        outcomes.Count(o => o.IsApproved).Should().Be(2);
        outcomes.Where(o => !o.IsApproved).Should().AllSatisfy(
            o => o.Rejected!.Reasons.Should().Contain(RejectionReason.DailyOrderAmountExceeded));
    }

    // 発行に失敗した後の再配送（別の接続）でも、コミット済みの予約から同じ承認を組み直す（拒否へ反転しない）。
    [Fact]
    public async Task 実DBで予約がコミットされていれば別の接続の再配送が同じ承認を組み直す()
    {
        var defaults = TradingDefaults.CreateSettings();
        var settings = defaults with { Limits = defaults.Limits with { MaxOpenPositions = 1 } };
        var decision = Decision("AAPL");

        ScreeningOutcome first;
        await using (var db = NewContext())
        {
            first = await Screening(db, settings).ScreenAsync(decision);
        }

        await using var db2 = NewContext();
        var redelivered = await Screening(db2, settings).ScreenAsync(decision);

        first.IsApproved.Should().BeTrue();
        redelivered.IsApprovedRepublish.Should().BeTrue();
        redelivered.Approved.Should().BeEquivalentTo(first.Approved);
    }

    // 臨界区間が例外で終わったら、その中で書いた予約はロールバックされて残らない（審査が落ちた＝承認は出ていない）。
    [Fact]
    public async Task 実DBで臨界区間が例外で終わると予約が残らない()
    {
        var id = Guid.NewGuid();
        await using (var db = NewContext())
        {
            var store = new EfEntryReservationStore(db);
            var act = () => store.RunSerializedAsync<int>(_ =>
            {
                store.Reserve(new EntryReservation(
                    id, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 10m, 1m, 10, DateTimeOffset.UtcNow,
                    StopLossExecutionMethod.BrokerStopOrder));
                throw new InvalidOperationException("発行前に落ちた");
            }, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var verify = NewContext();
        new EfEntryReservationStore(verify).Find(id).Should().BeNull();
    }

    // 注文源の最初の SQL 文を実行した直後に 1 回だけ割り込む（別の接続でコミットさせる）。
    private sealed class AfterFirstReaderInterceptor(Action inject) : DbCommandInterceptor
    {
        private int _fired;

        public int Readers { get; private set; }

        public override DbDataReader ReaderExecuted(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            Readers++;
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                inject();
            return result;
        }
    }

    // 🔴 T-10-2582（監査の指摘 1・実 DB で再現）: 予約から承認行へ引き継がれる瞬間に、注文源が 0 件と数えない。
    // 注文源の SQL 文の実行の直後に、別の接続から承認行（OrderApprovedLedgerHandler と同じ書き込み）をコミットさせる。
    // 承認行と予約を 2 つの文で読んでいた形では、READ COMMITTED の文ごとのスナップショットにより、
    // 1 文目（承認行）は承認行を見ず、2 文目（承認行の無い予約）は承認行を見て予約を外し、どちらにも現れなかった（1 件余分に承認される）。
    [Fact]
    public async Task 実DBで予約が承認行へ引き継がれる瞬間も注文源は1件と数える()
    {
        var decisionId = Guid.NewGuid();
        var approvedAt = DateTimeOffset.UtcNow.AddSeconds(-5);
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 10m);
        await using (var db = NewContext())
        {
            new EfEntryReservationStore(db).Reserve(new EntryReservation(
                decisionId, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, 10m, 1m, 10, approvedAt,
                StopLossExecutionMethod.BrokerStopOrder));
        }

        var interceptor = new AfterFirstReaderInterceptor(() =>
        {
            using var other = NewContext();
            new EfPortfolioLedgerStore(other).AppendApproval(decisionId, intent, approvedAt);
        });
        await using var reading = new RiskManagementDbContext(
            new DbContextOptionsBuilder<RiskManagementDbContext>()
                .UseNpgsql(_connectionString)
                .AddInterceptors(interceptor)
                .Options);

        var working = new EfWorkingEntryOrderSource(reading).GetWorkingEntryOrders(approvedAt.AddDays(-1));

        interceptor.Readers.Should().BeGreaterThan(0, "前提: 割り込みは注文源の SQL 文の実行の直後に起きた");
        working.Where(o => o.DecisionId == decisionId).Should().ContainSingle(
            "引き継ぎの瞬間も 0 件にしない（是正前は 2 文の間のコミットで 0 件になった）");
    }
}
