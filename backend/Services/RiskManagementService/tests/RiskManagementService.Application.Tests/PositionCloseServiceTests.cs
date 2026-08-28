using RiskManagementService.Application.Adapters;
using RiskManagementService.Application.Ports;
using RiskManagementService.Application.Services;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Application.Tests;

// FR-10, FR-11, UC-06, ADR-0003, #292, IADR-0117: 利用者（owner）による建玉の手仕舞い。
// 統制（kill switch/pause/ロックアウト）は本サービスの依存に含まれない＝手仕舞いを止めない（FR-10 本文）。
public class PositionCloseServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 6, 0, 0, TimeSpan.Zero);
    private const string Actor = "owner-1";

    // テスト用の現在値供給。要求された建玉のうち登録済みの (銘柄, 市場) だけを返す（未登録＝取得不能）。
    private sealed class FakeCurrentPriceSource : ICurrentPriceSource
    {
        private readonly Dictionary<(string, Market), decimal> _prices = [];

        public FakeCurrentPriceSource With(string symbol, Market market, decimal price)
        {
            _prices[(symbol, market)] = price;
            return this;
        }

        public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(
            IReadOnlyList<OpenPosition> positions)
        {
            var result = new Dictionary<(string Symbol, Market Market), decimal>();
            foreach (var p in positions)
            {
                if (_prices.TryGetValue((p.Symbol, p.Market), out var price))
                    result[(p.Symbol, p.Market)] = price;
            }

            return result;
        }
    }

    // AAPL を 100 株・単価 20 USD・レート 150 で建てた台帳。
    private static InMemoryPortfolioLedgerStore LedgerWithLong(
        int quantity = 100, TradeSide side = TradeSide.Buy, decimal fxRateToBase = 150m)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 20m, PositionEffect.Open, StopLossPrice: 19m, FxRateToBase: fxRateToBase),
            Now.AddDays(-1));
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", quantity, 20m, Now.AddDays(-1));
        return ledger;
    }

    // 台帳へ「承認済み・未約定（または部分約定）の決済」を積む。in-flight 判定の入力。
    private static void AppendPendingClose(
        InMemoryPortfolioLedgerStore ledger, int quantity, DateTimeOffset approvedAt, int filled = 0)
    {
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.InternalPaper,
                quantity, 21m, PositionEffect.Close, StopLossPrice: null, FxRateToBase: 150m),
            approvedAt);
        if (filled > 0)
            ledger.AppendFill(decisionId, $"close-{decisionId:N}", filled, 21m, approvedAt);
    }

    private static PositionCloseService Create(
        IPortfolioLedgerStore ledger,
        ICurrentPriceSource? prices = null,
        RiskManagementSettings? settings = null) =>
        new(ledger, new InMemoryRiskSettingsStore(settings),
            prices ?? new FakeCurrentPriceSource().With("AAPL", Market.UnitedStates, 21m),
            new FakeClock(Now, new DateOnly(2026, 7, 30)));

    private static PositionCloseCommand Command(int? quantity = null, decimal? limitPrice = null) =>
        new("AAPL", Market.UnitedStates, quantity, limitPrice, "手仕舞い");

    [Fact]
    public void 数量省略は保有全量の決済を発行する()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.Side.Should().Be(TradeSide.Sell, "ロングの手仕舞いは反対売買");
        outcome.Approval.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        outcome.Approval.Intent.Quantity.Should().Be(100);
        outcome.Approval.ApprovedQuantity.Should().Be(100);
        outcome.Approval.ApprovedAt.Should().Be(Now);
    }

    [Fact]
    public void 部分決済は指定数量だけを発行する()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(quantity: 40), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.Quantity.Should().Be(40);
    }

    [Fact]
    public void ショート建玉は買い決済で手仕舞う()
    {
        var outcome = Create(LedgerWithLong(side: TradeSide.Sell)).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.Side.Should().Be(TradeSide.Buy);
        outcome.Approval.Intent.Quantity.Should().Be(100);
    }

    [Fact]
    public void 建玉が無ければ拒否する()
    {
        var outcome = Create(new InMemoryPortfolioLedgerStore()).Request(Command(), Actor);

        outcome.Accepted.Should().BeFalse();
        outcome.Rejection.Should().Be(PositionCloseRejection.PositionNotFound);
        outcome.Approval.Should().BeNull();
        outcome.Requested.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 数量が非正なら拒否する(int quantity)
    {
        var outcome = Create(LedgerWithLong()).Request(Command(quantity), Actor);

        outcome.Rejection.Should().Be(PositionCloseRejection.InvalidQuantity);
    }

    [Fact]
    public void 保有を超える数量は拒否する()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(quantity: 101), Actor);

        outcome.Rejection.Should().Be(PositionCloseRejection.ExceedsAvailable);
    }

    // --- 多重投入ガード（本設計の要）: 台帳は約定でしか動かないため、処理中の決済を数える ---

    [Fact]
    public void 処理中の決済は利用可能数量から差し引く()
    {
        var ledger = LedgerWithLong();
        AppendPendingClose(ledger, quantity: 60, approvedAt: Now.AddMinutes(-5));

        // 保有 100・処理中 60 → 利用可能 40。50 は在庫超過。
        Create(ledger).Request(Command(quantity: 50), Actor)
            .Rejection.Should().Be(PositionCloseRejection.ExceedsAvailable);
    }

    [Fact]
    public void 数量省略時は処理中を除いた残りだけを決済する()
    {
        var ledger = LedgerWithLong();
        AppendPendingClose(ledger, quantity: 60, approvedAt: Now.AddMinutes(-5));

        var outcome = Create(ledger).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.Quantity.Should().Be(40, "全量指定でも処理中は二重に出さない");
    }

    [Fact]
    public void 全量が処理中なら拒否する()
    {
        var ledger = LedgerWithLong();
        AppendPendingClose(ledger, quantity: 100, approvedAt: Now.AddMinutes(-5));

        Create(ledger).Request(Command(), Actor)
            .Rejection.Should().Be(PositionCloseRejection.ExceedsAvailable);
    }

    [Fact]
    public void 窓を過ぎた滞留の承認は利用可能数量を減らさない()
    {
        // #270 破損期のような「永久に約定しない承認」が建玉を恒久的にロックするのを防ぐ。
        var ledger = LedgerWithLong();
        AppendPendingClose(ledger, quantity: 100, approvedAt: Now - PositionCloseService.DefaultInFlightWindow.Add(TimeSpan.FromMinutes(1)));

        var outcome = Create(ledger).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.Quantity.Should().Be(100);
    }

    [Fact]
    public void 部分約定した決済は未約定ぶんだけを処理中として数える()
    {
        var ledger = LedgerWithLong();
        AppendPendingClose(ledger, quantity: 60, approvedAt: Now.AddMinutes(-5), filled: 20);

        // 建玉は約定ぶん 20 減って 80。処理中は 60-20=40 → 利用可能 40。
        var outcome = Create(ledger).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.Quantity.Should().Be(40);
    }

    // --- 価格 ---

    [Fact]
    public void 指値が指定されればそれを用いる()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(limitPrice: 25m), Actor);

        outcome.Approval!.Intent.Price.Should().Be(25m);
    }

    [Fact]
    public void 指値省略時は現在値を用いる()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(), Actor);

        outcome.Approval!.Intent.Price.Should().Be(21m);
    }

    [Fact]
    public void 指値も現在値も無ければ拒否する()
    {
        // 価格 0 の注文を投げない（ブローカが拒否するか、成行相当で意図しない価格で約定する）。
        var outcome = Create(LedgerWithLong(), prices: new FakeCurrentPriceSource()).Request(Command(), Actor);

        outcome.Rejection.Should().Be(PositionCloseRejection.PriceUnavailable);
    }

    [Fact]
    public void 非正の指値は拒否する()
    {
        Create(LedgerWithLong()).Request(Command(limitPrice: 0m), Actor)
            .Rejection.Should().Be(PositionCloseRejection.PriceUnavailable);
    }

    // --- Intent の付帯情報 ---

    [Fact]
    public void 決済は建玉の換算レートを引き継ぐ()
    {
        // IADR-0107: 引き継がないと決済レグだけ未換算で台帳へ積まれ、基準通貨の実現損益が桁で誤る。
        var outcome = Create(LedgerWithLong(fxRateToBase: 150m)).Request(Command(), Actor);

        outcome.Approval!.Intent.FxRateToBase.Should().Be(150m);
        outcome.Approval.Intent.Price.Should().Be(21m, "価格はローカル通貨（USD）のまま");
    }

    [Fact]
    public void 決済注文は損切り価格を持たない()
    {
        Create(LedgerWithLong()).Request(Command(), Actor)
            .Approval!.Intent.StopLossPrice.Should().BeNull();
    }

    [Fact]
    public void 決済の商品種別は現物とする()
    {
        Create(LedgerWithLong()).Request(Command(), Actor)
            .Approval!.Intent.ProductType.Should().Be(ProductType.Cash);
    }

    [Fact]
    public void 決済のモードは現行段階の動作モードを用いる()
    {
        var settings = TradingDefaults.CreateSettings() with
        {
            Stage = new StageSettings(TradingStage.Stage2MinimalLive, BrokerProvider.MoomooReal, 100_000m),
        };

        Create(LedgerWithLong(), settings: settings).Request(Command(), Actor)
            .Approval!.Intent.Mode.Should().Be(BrokerProvider.MoomooReal);
    }

    // --- 監査（FR-11）---

    [Fact]
    public void 要求イベントは承認と同一のDecisionIdで相関する()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(quantity: 30), Actor);

        outcome.Requested!.DecisionId.Should().Be(outcome.Approval!.DecisionId);
    }

    [Fact]
    public void 要求イベントに操作者と理由と数量が載る()
    {
        // OrderApproved はアクターも理由も持たない。これが無いと「誰が・なぜ落としたか」が監査に残らない。
        var outcome = Create(LedgerWithLong()).Request(Command(quantity: 30), Actor);

        var requested = outcome.Requested!;
        requested.Actor.Should().Be(Actor);
        requested.Reason.Should().Be("手仕舞い");
        requested.Symbol.Should().Be("AAPL");
        requested.Market.Should().Be(Market.UnitedStates);
        requested.Side.Should().Be(TradeSide.Sell);
        requested.Quantity.Should().Be(30);
        requested.Price.Should().Be(21m);
        requested.RequestedAt.Should().Be(Now);
    }

    [Fact]
    public void 要求ごとに異なるDecisionIdを採る()
    {
        // 損切り（IADR-0015・EventId 由来で決定的）と異なり、利用者の各要求は独立した注文である。
        var service = Create(LedgerWithLong());

        var first = service.Request(Command(quantity: 10), Actor);
        var second = service.Request(Command(quantity: 10), Actor);

        second.Approval!.DecisionId.Should().NotBe(first.Approval!.DecisionId);
    }

    [Fact]
    public void 判定は台帳へ書き込まない()
    {
        // 承認の記録は OrderApprovedLedgerConsumer（既存経路）が行う。本サービスは読むだけ。
        var ledger = LedgerWithLong();
        var before = ledger.GetFills().Count;

        Create(ledger).Request(Command(), Actor);

        ledger.GetFills().Should().HaveCount(before);
        ledger.GetInFlightCloseQuantity("AAPL", Market.UnitedStates, Now.AddDays(-1)).Should().Be(0);
    }
}
