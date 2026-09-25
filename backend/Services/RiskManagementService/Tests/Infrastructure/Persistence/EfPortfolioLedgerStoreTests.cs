using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-05, IADR-0018: 取引台帳 EF ストアの永続化・相関・冪等を InMemory DB で検証する。
public class EfPortfolioLedgerStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static OrderIntent BuyIntent(int qty, decimal price) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, price);

    [Fact]
    public void 承認_Intent_の損切り価格を約定に補完して返す()
    {
        // IADR-0035: 損切り価格（権威データ）が ApprovedOrderRow に永続化され、LedgerFill に補完される。
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        var intent = new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 1_000m, PositionEffect.Open, 950m);

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, intent, DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2).GetFills().Single().StopLossPrice.Should().Be(950m);
    }

    // 🔴 **#563, IADR-0269**: 約定に DecisionId を載せる。報告書の日報 §2「判断根拠（要約）」が、
    // 監査台帳の TradeDecisionMade を**この鍵で**引く。載っていないと全行が未供給になる。
    [Fact]
    public void 約定に相関キー_DecisionId_を載せて返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        var fill = new EfPortfolioLedgerStore(db2).GetFills().Single();
        fill.DecisionId.Should().Be(decisionId);
        // 🔴 **対の否定形**: 既定値（相関できない）のまま返さない。
        fill.DecisionId.Should().NotBe(Guid.Empty);
    }

    // 🔴 **2 実装のドリフトを防ぐ。** InMemory 実装（テスト・開発既定）が相関キーを落とすと、
    // EF 実装だけが正しい状態になり、テストでは再現しない未供給が本番でだけ直る／壊れる。
    [Fact]
    public void InMemory実装も同じ相関キーを載せる()
    {
        var decisionId = Guid.NewGuid();
        var store = new InMemoryPortfolioLedgerStore();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);

        store.GetFills().Single().DecisionId.Should().Be(decisionId);
    }

    [Fact]
    public void 承認と約定を記録すると相関済みの約定を返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow).Should().BeTrue();
        }

        using var db2 = NewContext(dbName);
        var fills = new EfPortfolioLedgerStore(db2).GetFills();
        fills.Should().HaveCount(1);
        fills[0].Symbol.Should().Be("AAPL");
        fills[0].Side.Should().Be(TradeSide.Buy);
        fills[0].PositionEffect.Should().Be(PositionEffect.Open);
        fills[0].Quantity.Should().Be(10);
        fills[0].Price.Should().Be(1_050m);
    }

    [Fact]
    public void 承認のない約定は記録されず_false_を返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);

        store.AppendFill(Guid.NewGuid(), "ORD-X", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeFalse();
        store.GetFills().Should().BeEmpty();
    }

    [Fact]
    public void 同一_OrderId_の再送は重複記録しない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);

        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeTrue();
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow).Should().BeTrue(); // 再送

        store.GetFills().Should().HaveCount(1);
    }

    // #270, IADR-0113: 約定数量は累積値。同一 OrderId は累積が増えたときだけ更新する（差分加算しない）。
    [Fact]
    public void 同一_OrderId_の累積約定は一行のまま最新値へ更新される()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        store.AppendApproval(decisionId, BuyIntent(1_000, 340m), t0);

        store.AppendFill(decisionId, "ORD-1", 300, 340.5m, t0.AddSeconds(30)).Should().BeTrue();
        store.AppendFill(decisionId, "ORD-1", 1_000, 340.8m, t0.AddSeconds(60)).Should().BeTrue();

        var fills = store.GetFills();
        fills.Should().HaveCount(1, "1 注文 = 1 行（差分行を追記しない＝二重計上しない）");
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);
        fills[0].ExecutedAt.Should().Be(t0.AddSeconds(60));
    }

    [Fact]
    public void 少ない数量の後追いでは約定が巻き戻らない()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        store.AppendApproval(decisionId, BuyIntent(1_000, 340m), t0);

        store.AppendFill(decisionId, "ORD-1", 1_000, 340.8m, t0.AddSeconds(60)).Should().BeTrue();
        // 順序が前後して届いた古いスナップショット（部分約定）。
        store.AppendFill(decisionId, "ORD-1", 300, 340.5m, t0.AddSeconds(30)).Should().BeTrue();

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(1_000);
        fills[0].Price.Should().Be(340.8m);
        fills[0].ExecutedAt.Should().Be(t0.AddSeconds(60));
    }

    [Fact]
    public void 同一_DecisionId_の承認再送は最初の内容を保持する()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();

        store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
        store.AppendApproval(decisionId, BuyIntent(999, 9_999m), DateTimeOffset.UtcNow); // 再送（無視）
        store.AppendFill(decisionId, "ORD-1", 10, 1_000m, DateTimeOffset.UtcNow);

        var fills = store.GetFills();
        fills.Should().HaveCount(1);
        fills[0].Quantity.Should().Be(10);
    }

    // ---- FR-06, FR-15, FR-20, #569, IADR-0149 決定1, IADR-0271: 実際に発注したアダプタの発注先 ----

    // 🔴 **否定形**: 発注先を渡さずに記録された約定（列追加前のレガシー行）は **null のまま**であり、
    // 承認 Intent の Mode（＝段階が定める既定の発注先）へフォールバックしない。
    // 推定で埋めると、実弾の約定が SIMULATE 列へ載る（またはその逆）。
    [Fact]
    public void 発注先を渡さない約定は発注先不明のままにする()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            // 承認 Intent の Mode は InternalPaper（BuyIntent の既定）。ここへ倒れてはならない。
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2).GetFills().Single().Provider.Should().BeNull();
    }

    // **対の肯定形**: 渡した発注先は永続化され、別コンテキストからも読める。
    [Fact]
    public void 実際に発注した発注先を永続化して返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2).GetFills().Single().Provider.Should().Be(BrokerProvider.MoomooSimulate);
    }

    // 🔴 続報（部分約定 → 全量約定）が発注先を運ばなくても、**既知の発注先を null へ戻さない**。
    [Fact]
    public void 続報が発注先を運ばなくても既知の値を消さない()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(100, 1_000m), t0);
            store.AppendFill(decisionId, "ORD-1", 30, 1_000m, t0, BrokerProvider.MoomooReal);
            store.AppendFill(decisionId, "ORD-1", 100, 1_010m, t0.AddSeconds(30));
        }

        using var db2 = NewContext(dbName);
        var fill = new EfPortfolioLedgerStore(db2).GetFills().Single();
        fill.Quantity.Should().Be(100);
        fill.Provider.Should().Be(BrokerProvider.MoomooReal);
    }

    // FR-06, FR-16, #611, IADR-0286 決定1: 承認時点の認識時レート（1 USD あたりの円）が approved_orders に永続化され、
    // 約定（LedgerFill）へ補完される。報告書の為替差損益の根である。
    [Fact]
    public void 承認_Intent_の認識時レートを約定に補完して返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow, fxRateBaseToDisplay: 159.38m);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2).GetFills().Single().FxRateBaseToDisplay.Should().Be(159.38m);
    }

    // 🔴 否定形: 列追加前の行・承認時に解決できなかった行は **null のまま**返す（FxRateToBase の `?? 1m` とは違い、
    // 既定へ倒す正当な値が無い）。推定で埋めない——報告書が「未記録 N 件」と明記する。
    [Fact]
    public void 認識時レートが未記録の承認はnullのまま返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();

        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            store.AppendApproval(decisionId, BuyIntent(10, 1_000m), DateTimeOffset.UtcNow);
            store.AppendFill(decisionId, "ORD-1", 10, 1_050m, DateTimeOffset.UtcNow);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2).GetFills().Single().FxRateBaseToDisplay.Should().BeNull();
    }

    // ---- 🔴 T-10-810, FR-10, FR-03, #936, IADR-0393（2026-09-25 追記）: ロットを発注の順に並べる鍵を実体の台帳から通す ----
    // 稼働環境の順: A（715 株・330.88）を先に承認・発注し、B（713 株・331.67）を後に承認・発注した。約定は B が先。
    // 713 株の減少のあと、発注執行には A が 2 株・B が 713 株残る（作成時刻の古い順に割り当てる）ので、
    // 市場監視へ返すラインは B の 331.67 でなければならない。台帳が承認時刻を射影へ渡さないと（EntryOrderedAt が null）、
    // 射影は約定時刻の順へ黙って戻り 330.88 を返す。
    private static readonly DateTimeOffset LiveDay = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    private static void AppendLiveLayout(RiskManagementService.Features.RiskManagement.IPortfolioLedgerStore store)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var close = Guid.NewGuid();
        store.AppendApproval(a, EntryIntent(715, 330.88m), LiveDay.Add(new TimeSpan(13, 46, 45)));
        store.AppendApproval(b, EntryIntent(713, 331.67m), LiveDay.Add(new TimeSpan(13, 51, 48)));
        store.AppendFill(b, "ORD-B", 713, 337.98m, LiveDay.Add(new TimeSpan(13, 51, 49)));
        store.AppendFill(a, "ORD-A", 715, 337.555m, LiveDay.Add(new TimeSpan(13, 53, 0)));
        store.AppendApproval(close, new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper, 713, 333m,
            PositionEffect.Close), LiveDay.Add(new TimeSpan(15, 0, 0)));
        store.AppendFill(close, "ORD-C", 713, 333m, LiveDay.Add(new TimeSpan(15, 0, 1)));
    }

    private static OrderIntent EntryIntent(int qty, decimal stopLoss) =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, qty, 338m,
            PositionEffect.Open, stopLoss);

    [Fact]
    public void 承認はAが先で約定はBが先の配置を実体の台帳から公開すると331_67を返す()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
            AppendLiveLayout(new EfPortfolioLedgerStore(db));

        using var db2 = NewContext(dbName);
        var store = new EfPortfolioLedgerStore(db2);
        store.GetFills().Where(f => f.PositionEffect == PositionEffect.Open).Select(f => f.EntryOrderedAt).Should().BeEquivalentTo(
            new DateTimeOffset?[] { LiveDay.Add(new TimeSpan(13, 46, 45)), LiveDay.Add(new TimeSpan(13, 51, 48)) },
            "承認の ApprovedAt を約定へ載せる（null＝約定時刻の順へ戻る）");

        var view = new RiskManagementService.Features.RiskManagement.GetOpenPositions.OpenPositionsService(store).Build().Single();
        view.Quantity.Should().Be(715);
        view.StopLossPrice.Should().Be(331.67m);
    }

    // 🔴 2 実装のドリフトを防ぐ（InMemory 実装はテスト・開発の既定である）。
    [Fact]
    public void InMemory実装も承認はAが先で約定はBが先の配置で331_67を返す()
    {
        var store = new InMemoryPortfolioLedgerStore();
        AppendLiveLayout(store);

        var view = new RiskManagementService.Features.RiskManagement.GetOpenPositions.OpenPositionsService(store).Build().Single();
        view.Quantity.Should().Be(715);
        view.StopLossPrice.Should().Be(331.67m);
    }
}
