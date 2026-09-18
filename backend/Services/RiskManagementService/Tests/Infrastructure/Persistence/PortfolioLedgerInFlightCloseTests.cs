using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #292, IADR-0117: 処理中（承認済み・未約定）の決済数量。EfPortfolioLedgerStore と同一の意味論を
// インメモリ実装側で固定する（同名テストが Worker.Tests 側にもあり、両実装の乖離を検知する）。
public class PortfolioLedgerInFlightCloseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Window = Now.AddMinutes(-30);

    private static Guid Approve(
        InMemoryPortfolioLedgerStore ledger,
        PositionEffect effect,
        int quantity,
        DateTimeOffset approvedAt,
        string symbol = "AAPL",
        Market market = Market.UnitedStates)
    {
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(symbol, market, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 21m, effect, StopLossPrice: null, FxRateToBase: 1m),
            approvedAt);
        return decisionId;
    }

    [Fact]
    public void 承認も約定も無ければゼロ()
    {
        new InMemoryPortfolioLedgerStore()
            .GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 未約定の決済承認を数える()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    [Fact]
    public void 複数の未約定決済を合計する()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        Approve(ledger, PositionEffect.Close, 15, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(75);
    }

    [Fact]
    public void 部分約定は未約定ぶんだけを数える()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 20, 21m, Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);
    }

    [Fact]
    public void 全量約定した決済は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 60, 21m, Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 約定が承認数量を超えても負にはしない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 70, 21m, Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 新規建ての承認は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Open, 60, Now.AddMinutes(-5));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 窓より前に承認された決済は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-31));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    [Fact]
    public void 別銘柄と別市場は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5), symbol: "MSFT");
        Approve(ledger, PositionEffect.Close, 30, Now.AddMinutes(-5), market: Market.Japan);

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // --- #848: 終端になった承認を除く（取消・失効・拒否）。EF 実装と同一の観点を両側で固定する ---

    // T-10-400, #848: 稼働環境の実測そのもの。利用者が moomoo アプリで手仕舞いを取り消したあと、
    // 30 分の窓を待たずに数量が在庫へ戻ること。戻らないと下落局面で損切りできない（無保護の建玉 3,381 株）。
    [Fact]
    public void 取消が確認できた決済承認は数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 3_381, Now.AddMinutes(-5));
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(3_381);

        ledger.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-401, #848: 終端は 3 値とも同じ扱い（取消・失効・拒否）。当日注文の引け後失効も在庫を返す。
    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Rejected)]
    public void 終端になった決済承認は状態を問わず数えない(OrderStatus terminal)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));

        ledger.MarkTerminal(id, terminal, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // T-10-401, #848: 部分約定のまま取消された承認は**丸ごと**除く。残り 40 株は二度と約定しない一方、
    // 約定した 20 株は trade_fills 経由で建玉数量へ既に反映されている（二重に引かない）。
    [Fact]
    public void 部分約定のまま取消された承認は残数量も数えない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.AppendFill(id, "order-1", 20, 21m, Now.AddMinutes(-4));
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(40);

        ledger.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-402, #848（否定形・最重要）: 非終端の状態で終端を捏造しない。
    // 除外し過ぎると二重決済で意図しないショート化を作る。不明・進行中は処理中のまま。
    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.PartiallyFilled)]
    public void 非終端の状態では処理中のままにする(OrderStatus pending)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));

        ledger.MarkTerminal(id, pending, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // 🔴 T-10-402, #848（否定形）: 終端が一度も届いていない承認は従来どおり全量が処理中である
    //（「取消が届いていない」を「取り消された」と読まない）。
    [Fact]
    public void 終端が届いていない承認は処理中のままにする()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }

    // T-10-405, #848: 単調・冪等。後着の非終端で戻らず、同じ終端の再送でも結果が動かない。
    [Fact]
    public void 終端は単調で後着の非終端では戻らない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var id = Approve(ledger, PositionEffect.Close, 60, Now.AddMinutes(-5));
        ledger.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-2));

        ledger.MarkTerminal(id, OrderStatus.Accepted, Now.AddMinutes(-1));
        ledger.MarkTerminal(id, OrderStatus.Cancelled, Now.AddMinutes(-1));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(0);
    }

    // 🔴 T-10-405, #848（否定形）: 相関する承認が無い終端は**書かない**（AppendFill と同じ）。
    // 後から同じ DecisionId の承認が届いたら、終端は未確認＝処理中として数える（安全側へ倒れる）。
    [Fact]
    public void 承認より先に届いた終端は記録しない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();

        ledger.MarkTerminal(decisionId, OrderStatus.Cancelled, Now.AddMinutes(-5));
        ledger.AppendApproval(
            decisionId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                BrokerProvider.InternalPaper, 60, 21m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 1m),
            Now.AddMinutes(-4));

        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Window).Should().Be(60);
    }
}
