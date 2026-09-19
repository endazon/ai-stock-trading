using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #292, IADR-0117: 処理中（承認済み・未約定）の決済数量。Application 側の
// PortfolioLedgerInFlightCloseTests（InMemory 実装）と同一の観点を EF 実装でも固定し、両実装の乖離を検知する。
public class EfPortfolioLedgerInFlightCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = Now.AddMinutes(-30);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static Guid Approve(
        EfPortfolioLedgerStore store,
        PositionEffect effect,
        int quantity,
        DateTimeOffset approvedAt,
        string symbol = "AAPL",
        Market market = Market.UnitedStates)
    {
        var decisionId = Guid.NewGuid();
        store.AppendApproval(
            decisionId,
            new OrderIntent(symbol, market, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 21m, effect, StopLossPrice: null, FxRateToBase: 1m),
            approvedAt);
        return decisionId;
    }

    [Fact]
    public void 承認も約定も無ければゼロ()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPortfolioLedgerStore(db)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 未約定の決済承認を数える()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    [Fact]
    public void 複数の未約定決済を合計する()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        Approve(store, PositionEffect.Close, 15, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(75);
    }

    [Fact]
    public void 部分約定は未約定ぶんだけを数える()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }

    [Fact]
    public void 全量約定した決済は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 60, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 約定が承認数量を超えても負にはしない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 70, 21m, Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 新規建ての承認は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Open, 60, Now.AddMinutes(-5));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 窓より前に承認された決済は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-31));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別銘柄と別市場は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5), symbol: "MSFT");
        Approve(store, PositionEffect.Close, 30, Now.AddMinutes(-5), market: Market.Japan);

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // --- #848: 終端になった承認を除く（InMemory 実装の同名テストと同一の観点） ---

    // T-10-400, #848: 稼働環境の実測そのもの（利用者が moomoo アプリで取り消した手仕舞い）。
    [Fact]
    public void 取消が確認できた決済承認は数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 3_381, Now.AddMinutes(-5));
        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(3_381);

        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-401, #848: 終端は 3 値とも同じ扱い（取消・失効・拒否）。
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Rejected)]
    public void 終端になった決済承認は状態を問わず数えない(OrderStatus terminal)
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, terminal, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-401, #848: 部分約定のまま取消された承認は丸ごと除く（残りは二度と約定しない）。
    [Fact]
    public void 部分約定のまま取消された承認は残数量も数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));
        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);

        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-402, #848（否定形・最重要）: 非終端の状態で終端を捏造しない（除外し過ぎるとショート化する）。
    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public void 非終端の状態では処理中のままにする(OrderStatus pending)
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, pending, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // T-10-405, #848: 単調・冪等（後着の非終端で戻らない）。
    [Fact]
    public void 終端は単調で後着の非終端では戻らない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-2));

        store.MarkTerminal(id, OrderStatus.Accepted, Now.AddMinutes(-1));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-405, #848（否定形）: 相関する承認が無い終端は書かない。後着の承認は処理中として数える（安全側）。
    [Fact]
    public void 承認より先に届いた終端は記録しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();

        store.MarkTerminal(decisionId, OrderStatus.Cancelled, Now.AddMinutes(-5));
        store.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.InternalPaper, 60, 21m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m),
            Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // T-10-400, #848: 終端は永続化される（別コンテキストで読み直しても在庫が戻ったまま）。
    [Fact]
    public void 終端は別コンテキストで読み直しても残る()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid id;
        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
            store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-406, #848（監査ブロッキング B1・否定形）: **全量約定は在庫解放の終端ではない**
    //（InMemory 実装の同名テストと同一の観点）。EF 実装では MarkTerminal と AppendFill が別々の
    // SaveChanges であるため、Filled を終端に入れると建玉が丸ごと空いて見える区間が実在する。
    [Fact]
    public void 全量約定の終端は記録せず処理中のままにする()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, OrderStatus.Filled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);

        // 約定が載れば自然に 0 になる（＝Filled を終端に入れる必要がそもそも無い）。
        store.AppendFill(id, "ORD-1", 60, 21m, Now.AddMinutes(-1));
        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-406, #848: 全量約定を無視しても門を閉じ切らない —— そのあとに本物の終端（取消）が来れば記録する。
    [Fact]
    public void 全量約定を無視した後でも本物の終端は記録する()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkTerminal(id, OrderStatus.Filled, Now.AddMinutes(-2));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // --- T-10-410, #852, IADR-0356: 見送り（発注していない）は処理中から外す（InMemory と同一の意味論） ---

    // T-10-410（肯定形・主目的）: 見送られた決済は窓を待たずに在庫へ戻り、**永続化される**
    //（別コンテキストで読み直しても戻ったまま）。
    [Fact]
    public void 見送られた決済承認は処理中から外れ別コンテキストでも残る()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid id;
        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
            store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);

            store.MarkForgone(id, Now.AddMinutes(-1));

            store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-410, #852, IADR-0356: 見送りは**注文状態を持たない**（IADR-0211。証券会社に注文が存在しない）。
    // 判定に使う TerminalAt だけが立ち、診断用の TerminalStatus は **null のまま**である
    //（`TerminalAt is not null && TerminalStatus is null` が「見送り」の表現になる）。
    // 取消・拒否を捏造すると、FR-05 の「拒否」の別集計が接続障害で汚染される。
    [Fact]
    public void 見送りは注文状態を捏造しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));

        store.MarkForgone(id, Now.AddMinutes(-1));

        var row = db.ApprovedOrders.Find(id)!;
        row.TerminalAt.Should().Be(Now.AddMinutes(-1));
        row.TerminalStatus.Should().BeNull("見送りは証券会社に存在しない注文であり、注文状態を持たない");
    }

    // 🔴 T-10-410（否定形）: 相関する承認が無い見送りは書かない。後着の承認は処理中として数える（安全側）。
    [Fact]
    public void 承認より先に届いた見送りは記録しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var decisionId = Guid.NewGuid();

        store.MarkForgone(decisionId, Now.AddMinutes(-5));
        store.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.InternalPaper, 60, 21m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m),
            Now.AddMinutes(-4));

        store.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // T-10-410（単調・冪等）: 先に本物の終端が立っていれば見送りは上書きしない（時刻も状態も動かさない）。
    [Fact]
    public void 先に記録された終端を見送りで上書きしない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPortfolioLedgerStore(db);
        var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
        store.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-3));

        store.MarkForgone(id, Now.AddMinutes(-1));

        var row = db.ApprovedOrders.Find(id)!;
        row.TerminalAt.Should().Be(Now.AddMinutes(-3), "最初の終端が真（単調）");
        row.TerminalStatus.Should().Be(OrderStatus.Cancelled);
    }

    // 🔴 T-10-410, #852, #881, IADR-0356 / IADR-0357（否定形・最重要・マージ順に依存しないこと）:
    // **`TerminalAt` が並行トークンになっても `MarkForgone` は例外を投げ抜けない。**
    //
    // 見送り（`OrderDispatchForgone`）と終端（`OrderCancelled` / `OrderExecuted`）は Wolverine の別キュー
    // ＝並行に走る。#881 が `TerminalAt` を並行トークンにすると、負けた側の UPDATE は 0 行になり
    // `DbUpdateConcurrencyException` になる。投げ抜けると `OrderDispatchForgoneLedgerHandler` を貫通して
    // Wolverine の再試行 → error キューへ至り、**見送りが記録されず在庫が解放されない**（#852 の実害の再発）。
    //
    // ここでは #881 の並行トークンだけを `IModelCustomizer` で再現し（本ブランチの DbContext は変えない）、
    // 「読んだあとに別コンテキストが終端を書く」競合を**決定的に**起こす。
    // 🔴 本ブランチのままでもトークンが無いため投げないが、この試験は**トークンが入った後の世界**を固定する
    //（＝#881 と本 PR のマージ順に依存しなくなる）。
    [Fact]
    public void 並行トークンが入っても見送りは例外を投げ抜けない()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid id;
        using (var seed = NewContextWithTerminalAtAsConcurrencyToken(dbName))
        {
            id = Approve(new EfPortfolioLedgerStore(seed), PositionEffect.Close, 60, Now.AddMinutes(-5));
        }

        using var loser = NewContextWithTerminalAtAsConcurrencyToken(dbName);
        var store = new EfPortfolioLedgerStore(loser);

        // 1) 負ける側が承認を読み込む（このとき TerminalAt は null＝トークンの元値）。
        loser.ApprovedOrders.Find(id).Should().NotBeNull();

        // 2) そのあいだに別コンテキスト（勝つ側）が終端を書き切る。
        using (var winner = NewContextWithTerminalAtAsConcurrencyToken(dbName))
        {
            new EfPortfolioLedgerStore(winner).MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-2));
        }

        // 3) 負ける側が見送りを書こうとする＝トークン不一致。**投げてはならない**（黙って何もしない）。
        var act = () => store.MarkForgone(id, Now.AddMinutes(-1));
        act.Should().NotThrow<DbUpdateConcurrencyException>(
            "見送りが例外で抜けると再試行 → error キューへ落ち、在庫が解放されないまま残る（#852 の実害の再発）");
        act.Should().NotThrow();

        // 4) 先に書かれた終端が残っている（単調。見送りが上書きも破壊もしていない）。
        using var verify = NewContextWithTerminalAtAsConcurrencyToken(dbName);
        var row = verify.ApprovedOrders.Find(id)!;
        row.TerminalAt.Should().Be(Now.AddMinutes(-2), "最初に記録された終端が真");
        row.TerminalStatus.Should().Be(OrderStatus.Cancelled, "負けた見送りが診断用の状態を消していない");
        new EfPortfolioLedgerStore(verify)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-410（上の試験の**陽性対照**）: 上が「投げない」ことを確かめられるのは、
    // **この仕掛けが本当に `DbUpdateConcurrencyException` を作っているとき**だけである。
    // まったく同じ手順を EF へ直接書かせると**投げる** —— つまり上の緑は
    // 「競合が起きていないから」ではなく「`MarkForgone` が捕まえているから」である。
    // これが赤くなったら（例: InMemory provider が並行トークンを検査しなくなった）、上の試験は
    // **空振りで緑**になっているので仕掛けを作り直すこと。
    [Fact]
    public void 並行トークンの再現が効いていることの対照()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid id;
        using (var seed = NewContextWithTerminalAtAsConcurrencyToken(dbName))
        {
            id = Approve(new EfPortfolioLedgerStore(seed), PositionEffect.Close, 60, Now.AddMinutes(-5));
        }

        using var loser = NewContextWithTerminalAtAsConcurrencyToken(dbName);
        var stale = loser.ApprovedOrders.Find(id)!;

        using (var winner = NewContextWithTerminalAtAsConcurrencyToken(dbName))
        {
            new EfPortfolioLedgerStore(winner).MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-2));
        }

        // MarkForgone が内側でやっているのと同じ書き込みを、catch 無しで行う。
        stale.TerminalAt = Now.AddMinutes(-1);
        var act = () => loser.SaveChanges();

        act.Should().Throw<DbUpdateConcurrencyException>(
            "この競合は実際に並行トークン違反を起こす（起こさないなら上の試験は空振りで緑になっている）");
    }

    // #881, IADR-0357 の `e.Property(r => r.TerminalAt).IsConcurrencyToken()` だけを再現する。
    // RiskManagementDbContext は sealed なので派生できない —— モデルの組み立てに割り込む。
    private static RiskManagementDbContext NewContextWithTerminalAtAsConcurrencyToken(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .ReplaceService<IModelCustomizer, TerminalAtConcurrencyTokenCustomizer>()
            .Options);

    private sealed class TerminalAtConcurrencyTokenCustomizer(ModelCustomizerDependencies dependencies)
        : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ApprovedOrderRow>().Property(r => r.TerminalAt).IsConcurrencyToken();
        }
    }

    [Fact]
    public void 別コンテキストで読み直しても同じ結果になる()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfPortfolioLedgerStore(db);
            var id = Approve(store, PositionEffect.Close, 60, Now.AddMinutes(-5));
            store.AppendFill(id, "ORD-1", 20, 21m, Now.AddMinutes(-4));
        }

        using var db2 = NewContext(dbName);
        new EfPortfolioLedgerStore(db2)
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }
}
