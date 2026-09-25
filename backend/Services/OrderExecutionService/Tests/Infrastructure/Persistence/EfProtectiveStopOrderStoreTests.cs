using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグ記録の永続化を InMemory DB で検証する
// （契約: EntryDecisionId upsert・Active の洗い出し・ラウンドトリップ）。
public class EfProtectiveStopOrderStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 6, 0, 0, TimeSpan.Zero);

    private static OrderExecutionDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static ProtectiveStopOrder Stop(Guid entryDecisionId, int attempt = 1,
        ProtectiveStopState state = ProtectiveStopState.Active, DateTimeOffset? createdAt = null) =>
        new(entryDecisionId, ProtectiveStopIds.StopDecisionId(entryDecisionId, attempt), $"stop-{attempt}",
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, 1m, attempt, state, createdAt ?? Now, Now);

    [Fact]
    public void 保存はラウンドトリップし別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        var stop = Stop(entryDecisionId);

        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(stop);
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId);
        found.Should().Be(stop);
    }

    [Fact]
    public void 同一エントリーへの再保存は上書きになる_再発注の試行置き換え()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            store.Save(Stop(entryDecisionId, attempt: 1));
            store.Save(Stop(entryDecisionId, attempt: 2));
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId);
        found!.Attempt.Should().Be(2);
        found.StopOrderId.Should().Be("stop-2");
        db2.ProtectiveStopOrders.Count().Should().Be(1, "1 エントリー = 高々 1 保護（最新試行のみ）");
    }

    [Fact]
    public void FindActiveはActiveだけを古い順に返しCompletedを含めない()
    {
        var dbName = Guid.NewGuid().ToString();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var done = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            store.Save(Stop(newer, createdAt: Now));
            store.Save(Stop(older, createdAt: Now.AddMinutes(-10)));
            store.Save(Stop(done, state: ProtectiveStopState.Completed));
        }

        using var db2 = NewContext(dbName);
        var active = new EfProtectiveStopOrderStore(db2).FindActive(10);
        active.Select(s => s.EntryDecisionId).Should().Equal(older, newer);
    }

    [Fact]
    public void FindActiveはバッチサイズで打ち切る()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            for (var i = 0; i < 5; i++)
                store.Save(Stop(Guid.NewGuid(), createdAt: Now.AddMinutes(i)));
        }

        using var db2 = NewContext(dbName);
        new EfProtectiveStopOrderStore(db2).FindActive(3).Should().HaveCount(3);
    }

    // FR-10, ADR-0040 決定1（S1）, #820, IADR-0344 決定1: 機構列と到達の記録が往復し、旧い行（列の既定）は S0 として読まれる。
    [Fact]
    public void ソフトウェア逆指値の機構と到達の記録はラウンドトリップする()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entryDecisionId, ProtectiveStopIds.SoftwareStopId(entryDecisionId), string.Empty, "AAPL",
            Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 2,
            ProtectiveStopState.Active, Now, Now, StopLossExecutionMethod.SoftwareStop, Now.AddMinutes(1), 940.25m);

        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(stop);
        }

        using var db2 = NewContext(dbName);
        new EfProtectiveStopOrderStore(db2).Find(entryDecisionId).Should().Be(stop);
    }

    [Fact]
    public void 機構を指定しない保護記録はS0として保存される()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(Stop(entryDecisionId));
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId)!;
        found.Mechanism.Should().Be(StopLossExecutionMethod.BrokerStopOrder);
        found.IsSoftwareStop.Should().BeFalse();
        found.TriggeredAt.Should().BeNull();
    }

    [Fact]
    public void FindActiveSoftwareStopsはActiveなS1の同一銘柄同一方向だけを古い順に返す()
    {
        var dbName = Guid.NewGuid().ToString();
        StopLossExecutionMethod s1 = StopLossExecutionMethod.SoftwareStop;
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            store.Save(Stop(newer, createdAt: Now) with { Mechanism = s1 });
            store.Save(Stop(older, createdAt: Now.AddMinutes(-5)) with { Mechanism = s1 });
            store.Save(Stop(Guid.NewGuid()));                                                          // S0
            store.Save(Stop(Guid.NewGuid(), state: ProtectiveStopState.Completed) with { Mechanism = s1 }); // 完了
            store.Save(Stop(Guid.NewGuid()) with { Mechanism = s1, Symbol = "MSFT" });                  // 別銘柄
            store.Save(Stop(Guid.NewGuid()) with { Mechanism = s1, EntrySide = TradeSide.Sell });       // 別方向
        }

        using var db2 = NewContext(dbName);
        new EfProtectiveStopOrderStore(db2).FindActiveSoftwareStops("AAPL", Market.UnitedStates, TradeSide.Buy)
            .Select(s => s.EntryDecisionId).Should().Equal(older, newer);
    }

    // T-10-378（受け入れ基準 32）: #820 の 4 巡目監査, IADR-0344 追記(4)。
    // 残保護数量は**状態**であり、毎巡回引き直さないことが設計の要である。永続化されなければ成立しない。
    [Fact]
    public void 残保護数量と据え置き通知の記録が往復する()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entryDecisionId, ProtectiveStopIds.SoftwareStopId(entryDecisionId), string.Empty, "AAPL",
            Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 1,
            ProtectiveStopState.Active, Now, Now, StopLossExecutionMethod.SoftwareStop, Now.AddMinutes(1), 940m,
            RemainingProtected: 4, StalledNotifiedAt: Now.AddMinutes(20));

        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(stop);
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId)!;
        found.Should().Be(stop);
        found.RemainingProtected.Should().Be(4);
        found.IsEntryFillConfirmed.Should().BeTrue();
        found.ProtectedQuantity.Should().Be(4);
        found.StalledNotifiedAt.Should().Be(Now.AddMinutes(20));
    }

    // T-10-436（受け入れ基準 43・44）: #820 の 8 巡目監査, IADR-0344 追記(8)。
    // 失効の連続観測回数と「1 株も動かせない状態」の記録も**状態**である。
    // 永続化されないと、再起動のたびに失効の数え直し・Critical の再送が起きる。
    [Fact]
    public void 失効の観測回数と保護停止の記録が往復する()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entryDecisionId, ProtectiveStopIds.SoftwareStopId(entryDecisionId), string.Empty, "AAPL",
            Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0,
            ProtectiveStopState.Active, Now, Now, StopLossExecutionMethod.SoftwareStop,
            RemainingProtected: 10, PendingExternalReduction: 10, ExternalReductionObservations: 1,
            ExternalReductionAbsences: 1, ProtectionSuspendedSince: Now.AddMinutes(3),
            ProtectionSuspendedNotifiedAt: Now.AddMinutes(18));

        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(stop);
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId)!;
        found.Should().Be(stop);
        found.ExternalReductionAbsences.Should().Be(1);
        found.ProtectionSuspendedSince.Should().Be(Now.AddMinutes(3));
        found.ProtectionSuspendedNotifiedAt.Should().Be(Now.AddMinutes(18));
        found.EffectiveProtectedQuantity.Should().Be(0);
        found.IsProtectionSuspended.Should().BeTrue("帳簿では 10 株を守っているのに 1 株も動かせない");
    }

    // 既定値（列を持たなかった時代の行と同じ姿）では「保護停止」ではない。
    [Fact]
    public void 未確定の観測が無い行は保護停止ではない()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(Stop(entryDecisionId));
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId)!;
        found.ExternalReductionAbsences.Should().Be(0);
        found.ProtectionSuspendedSince.Should().BeNull();
        found.IsProtectionSuspended.Should().BeFalse();
        // #820 の 10 巡目監査, IADR-0344 追記(9) 決定3: 既定値では「まだ知らせていない」。
        found.UnattributedNotifiedQuantity.Should().BeNull();
        found.UnattributedNotifiedAt.Should().BeNull();
    }

    // T-10-491（受け入れ基準 54 / #820 の 10 巡目監査, IADR-0344 追記(9) 決定3）:
    // 「帰属不明の建玉」を知らせた記録が往復する。再起動のたびに Warning を再送しないための記録であり、
    // 揮発させると**毎巡回（既定 30 秒）鳴って通知が埋もれる**。
    [Fact]
    public void 帰属不明の通知の記録が往復する()
    {
        var dbName = Guid.NewGuid().ToString();
        var entryDecisionId = Guid.NewGuid();
        var stop = new ProtectiveStopOrder(
            entryDecisionId, ProtectiveStopIds.SoftwareStopId(entryDecisionId), string.Empty, "AAPL",
            Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 0,
            ProtectiveStopState.Completed, Now, Now, StopLossExecutionMethod.SoftwareStop,
            RemainingProtected: 0,
            UnattributedNotifiedQuantity: 10, UnattributedNotifiedAt: Now.AddMinutes(7));

        using (var db = NewContext(dbName))
        {
            new EfProtectiveStopOrderStore(db).Save(stop);
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).Find(entryDecisionId)!;
        found.Should().Be(stop);
        found.UnattributedNotifiedQuantity.Should().Be(10);
        found.UnattributedNotifiedAt.Should().Be(Now.AddMinutes(7));
        found.State.Should().Be(
            ProtectiveStopState.Completed, "群の代表が完了済みの行でも記録を持つ（受理後に取消された決済の残りの配置）");
    }

    // 未確定（null）の S1 行は 1 株も主張しない。S0 の旧い行（列が無かった時代）は Quantity を主張する。
    [Fact]
    public void 残保護数量が未設定なら_S1は0を_S0はQuantityを主張する()
    {
        var dbName = Guid.NewGuid().ToString();
        var s1 = Guid.NewGuid();
        var s0 = Guid.NewGuid();
        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            store.Save(Stop(s1) with { Mechanism = StopLossExecutionMethod.SoftwareStop });
            store.Save(Stop(s0));
        }

        using var db2 = NewContext(dbName);
        var store2 = new EfProtectiveStopOrderStore(db2);
        store2.Find(s1)!.ProtectedQuantity.Should().Be(0, "約定が確定するまで建玉を主張しない");
        store2.Find(s1)!.IsEntryFillConfirmed.Should().BeFalse();
        store2.Find(s0)!.ProtectedQuantity.Should().Be(10, "S0 はブローカーに実在する逆指値が覆う数量を主張する");
    }

    // T-10-895, FR-10, #880, IADR-0412 決定2: 帰属不明の通知済みの印を持つ行を、状態を問わず更新が新しい順・上限つきで返す
    // （純額 0 になった群も検知が訪れてリセットするための問い合わせ）。EF（InMemory プロバイダ）とインメモリで同じ契約。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 帰属不明の通知済みの行を状態を問わず新しい順に上限つきで返す(bool ef)
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewContext(dbName);
        OrderExecutionService.Features.OrderExecution.IProtectiveStopOrderStore store = ef
            ? new EfProtectiveStopOrderStore(db)
            : new InMemoryProtectiveStopOrderStore();

        var plain = Guid.NewGuid();
        var completedQty = Guid.NewGuid();
        var activeAt = Guid.NewGuid();
        store.Save(Stop(plain));
        store.Save(Stop(completedQty, state: ProtectiveStopState.Completed) with
        {
            UnattributedNotifiedQuantity = 10,
            UpdatedAt = Now.AddMinutes(2),
        });
        // 片方の列だけが残った行も「通知済み」として返す（どちらか一方でも印である）。
        store.Save(Stop(activeAt) with { UnattributedNotifiedAt = Now, UpdatedAt = Now.AddMinutes(1) });

        store.FindUnattributedNotified(50).Select(s => s.EntryDecisionId)
            .Should().Equal([completedQty, activeAt], "印のある行だけを、更新が新しい順に返す");
        store.FindUnattributedNotified(1).Select(s => s.EntryDecisionId).Should().Equal([completedQty]);
    }

    // 🔴 T-10-1014（PR #999 の再監査 2）, FR-10, #879, IADR-0424 決定1: FindActiveFor の問い合わせの条件を EF（InMemory プロバイダ）で固定する
    // ——銘柄・市場・エントリー方向の一致、Active だけ、機構を問わない、件数の上限なし（上限つきの FindActive の 500 件を超えても返す）、古い順。
    [Fact]
    public void FindActiveForは銘柄市場方向が一致するActive行だけを機構を問わず上限なしで古い順に返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var expected = new List<Guid>();
        using (var db = NewContext(dbName))
        {
            var store = new EfProtectiveStopOrderStore(db);
            // 一致する Active 行 501 件（S0 と S1 を交互に。上限つきの FindActive の 500 件を超える）。
            for (var i = 0; i < 501; i++)
            {
                var id = Guid.NewGuid();
                var row = Stop(id, createdAt: Now.AddMinutes(i));
                store.Save(i % 2 == 0 ? row : row with { Mechanism = StopLossExecutionMethod.SoftwareStop, StopOrderId = string.Empty, RemainingProtected = 10 });
                expected.Add(id);
            }

            // 一致しない行（1 条件ずつ外す）。
            store.Save(Stop(Guid.NewGuid(), createdAt: Now.AddMinutes(-1)) with { Symbol = "MSFT" });
            store.Save(Stop(Guid.NewGuid(), createdAt: Now.AddMinutes(-2)) with { Market = Market.Japan });
            store.Save(Stop(Guid.NewGuid(), createdAt: Now.AddMinutes(-3)) with { EntrySide = TradeSide.Sell });
            store.Save(Stop(Guid.NewGuid(), state: ProtectiveStopState.Completed, createdAt: Now.AddMinutes(-4)));
        }

        using var db2 = NewContext(dbName);
        var found = new EfProtectiveStopOrderStore(db2).FindActiveFor("AAPL", Market.UnitedStates, TradeSide.Buy);

        found.Select(s => s.EntryDecisionId).Should().Equal(expected, "一致する Active 行だけを、上限なしで古い順に返す");
        found.Should().Contain(s => s.IsSoftwareStop).And.Contain(s => !s.IsSoftwareStop, "機構を問わない");
    }
}
