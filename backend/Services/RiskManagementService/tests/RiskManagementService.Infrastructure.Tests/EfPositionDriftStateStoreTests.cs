using RiskManagementService.Application.Ports;
using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Infrastructure.Tests;

// FR-05, FR-10, #305, IADR-0124: 建玉乖離の追跡状態ストア（単一行）の EF 永続化を InMemory DB で検証する。
// 別コンテキストは「別レプリカ／再起動」の代理。連続観測回数と報告済みシグネチャが跨いで一貫すること、
// 並行更新に負けた側が**何も書かずに** false を返すこと（ロストアップデートを起こさないこと）を担保する。
public class EfPositionDriftStateStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 未記録時は初期状態を返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPositionDriftStateStore(db).Get().Should().Be(PositionDriftState.Initial);
    }

    [Fact]
    public void 保存した追跡状態は別コンテキストからも読める_durable()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfPositionDriftStateStore(db)
                .TrySave(new PositionDriftState("AAPL:1:100:80:2", 1, string.Empty, 0))
                .Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var state = new EfPositionDriftStateStore(db2).Get();

        state.ObservedSignature.Should().Be("AAPL:1:100:80:2");
        state.ConsecutiveCount.Should().Be(1, "別レプリカが続きから数えられなければ連続条件は永久に満たされない");
        state.Version.Should().Be(1);
    }

    [Fact]
    public void 別コンテキストが続きから数えて報告済みを記録できる()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfPositionDriftStateStore(db).TrySave(new PositionDriftState("SIG", 1, string.Empty, 0));
        }

        using (var db2 = NewContext(dbName))
        {
            var store = new EfPositionDriftStateStore(db2);
            var current = store.Get();
            store.TrySave(current with { ConsecutiveCount = 2, ReportedSignature = "SIG" }).Should().BeTrue();
        }

        using var db3 = NewContext(dbName);
        var state = new EfPositionDriftStateStore(db3).Get();

        state.ConsecutiveCount.Should().Be(2);
        state.ReportedSignature.Should().Be("SIG");
    }

    [Fact]
    public void 古い版での保存は失敗する_ロストアップデートを起こさない()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfPositionDriftStateStore(db).TrySave(new PositionDriftState("SIG", 1, string.Empty, 0));
        }

        using var db2 = NewContext(dbName);
        var store = new EfPositionDriftStateStore(db2);

        // 版 0 は既に版 1 へ進んでいる（別レプリカが先に保存した状況）。
        store.TrySave(new PositionDriftState("STALE", 9, "STALE", 0)).Should().BeFalse();

        var state = store.Get();
        state.ObservedSignature.Should().Be("SIG", "負けた側は何も書かない");
        state.ConsecutiveCount.Should().Be(1);
    }

    [Fact]
    public void 同じ版から同時に保存すると片方だけが勝つ()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var seed = NewContext(dbName))
        {
            new EfPositionDriftStateStore(seed).TrySave(new PositionDriftState("SIG", 1, string.Empty, 0));
        }

        // 2 レプリカが同じ版を読んだ状態を、2 つのコンテキストで再現する。
        using var dbA = NewContext(dbName);
        using var dbB = NewContext(dbName);
        var storeA = new EfPositionDriftStateStore(dbA);
        var storeB = new EfPositionDriftStateStore(dbB);
        var readA = storeA.Get();
        var readB = storeB.Get();

        var savedA = storeA.TrySave(readA with { ConsecutiveCount = 2, ReportedSignature = "SIG" });
        var savedB = storeB.TrySave(readB with { ConsecutiveCount = 2, ReportedSignature = "SIG" });

        savedA.Should().BeTrue();
        savedB.Should().BeFalse("同じ版からの二重更新は並行トークンが弾く");

        using var db3 = NewContext(dbName);
        new EfPositionDriftStateStore(db3).Get().Version.Should().Be(2, "版は 1 回だけ進む");
    }

    [Fact]
    public void 初回行を別レプリカが先に作っていたら負ける()
    {
        // 未記録（版 0）を読んだ後に別レプリカが行を作った状況。実 DB では両者が同時に INSERT して
        // 主キー衝突（23505）になる経路もあるが、共有 InMemory では後発の Find が既に行を見つけるため
        // 版不一致として弾かれる。どちらも「負けた」＝ false で、例外は呼び出し側へ漏らさない。
        var dbName = Guid.NewGuid().ToString();

        using var dbA = NewContext(dbName);
        using var dbB = NewContext(dbName);
        var storeA = new EfPositionDriftStateStore(dbA);
        var storeB = new EfPositionDriftStateStore(dbB);

        storeA.Get().Should().Be(PositionDriftState.Initial);
        storeB.Get().Should().Be(PositionDriftState.Initial);

        storeA.TrySave(new PositionDriftState("A", 1, string.Empty, 0)).Should().BeTrue();

        var act = () => storeB.TrySave(new PositionDriftState("B", 1, string.Empty, 0));

        act.Should().NotThrow().Which.Should().BeFalse();
        storeA.Get().ObservedSignature.Should().Be("A", "勝った側の状態が残る");
    }
}
