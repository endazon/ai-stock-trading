using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.ClosePosition;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-10, FR-11, UC-06, ADR-0003, #847, IADR-0357: **手仕舞いは既定で成行**。
//
// 稼働環境（2026-09-18 23:13 JST）の実測: 3,381 株の手仕舞いを limitPrice 省略で要求したところ、
// その時点の現在値 334.09 の**売り指値**が出た。直後に 333.59 まで下げ、約定しないまま板に残り、
// 板に残った注文が数量を占めるため（ExceedsAvailable）指値を変えた出し直しもできなくなった。
// 現在値の指値は、価格が下げ続けるかぎり置いていかれる —— **手仕舞いが必要な場面でこそ効かない**。
//
// 本ファイルは「limitPrice を省いたら成行」「明示の指値は従来どおり」「矛盾した指定は拒否」を固定する。
// 既存の PositionCloseServiceTests（T-10-400〜403 を含む在庫ガードの固定）は触らない。
public class PositionCloseMarketOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);
    private const string Actor = "owner-1";

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

    // 稼働環境の再現に寄せた台帳: 3,381 株・平均取得単価 334.09（米国株なのでレートは 1）。
    private static InMemoryPortfolioLedgerStore LedgerWithLong(int quantity = 3381, decimal entryPrice = 334.09m)
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var decisionId = Guid.NewGuid();
        ledger.AppendApproval(
            decisionId,
            new OrderIntent(
                "SOXL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate,
                quantity, entryPrice, PositionEffect.Open, StopLossPrice: 320m),
            Now.AddHours(-1));
        ledger.AppendFill(decisionId, $"open-{decisionId:N}", quantity, entryPrice, Now.AddHours(-1));
        return ledger;
    }

    private static PositionCloseService Create(IPortfolioLedgerStore ledger, ICurrentPriceSource? prices = null) =>
        new(ledger, new InMemoryRiskSettingsStore(null),
            prices ?? new FakeCurrentPriceSource().With("SOXL", Market.UnitedStates, 333.59m),
            new FakeClock(Now, new DateOnly(2026, 9, 19)));

    private static PositionCloseCommand Command(decimal? limitPrice = null, bool? marketOrder = null) =>
        new("SOXL", Market.UnitedStates, null, limitPrice, "手仕舞い", marketOrder);

    // 🔴 #847（本 issue の中核）: limitPrice を省いた手仕舞いは**成行**である。
    [Fact]
    public void 指値省略の手仕舞いは成行で出す()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.MarketOrder.Should().BeTrue(
            "現在値の指値は下落局面で置いていかれる（#847 の実害）。手仕舞いの既定は成行である");
        outcome.Approval.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        outcome.Approval.Intent.Quantity.Should().Be(3381);
    }

    // 成行でも参照価格は載せる（台帳・監査・通知・paper の約定価格が使う）。現在値が取れるならそれ。
    [Fact]
    public void 成行でも参照価格として現在値を載せる()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(), Actor);

        outcome.Approval!.Intent.Price.Should().Be(333.59m);
        outcome.Requested!.Price.Should().Be(333.59m);
    }

    // 🔴 #847: **現在値が取れなくても手仕舞いを止めない。** 成行は価格をブローカーへ送らないため、
    // 参照価格が無いことを理由に拒否するのは「下落局面で手仕舞えない」を作るだけである。
    [Fact]
    public void 現在値が取れなくても成行の手仕舞いは通る()
    {
        var outcome = Create(LedgerWithLong(), prices: new FakeCurrentPriceSource()).Request(Command(), Actor);

        outcome.Accepted.Should().BeTrue("成行は価格を送らない。価格が読めないことは手仕舞いを止める理由にならない");
        outcome.Approval!.Intent.MarketOrder.Should().BeTrue();
        outcome.Approval.Intent.Price.Should().Be(
            334.09m, "参照価格は台帳が持つ平均取得単価へ倒す（0 を載せない・値を捏造しない）");
    }

    [Fact]
    public void 指値を指定したら従来どおり指値で出す()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(limitPrice: 335m), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.MarketOrder.Should().BeFalse();
        outcome.Approval.Intent.Price.Should().Be(335m);
    }

    // 旧既定（現在値の指値）を明示的に選べる退避口。
    [Fact]
    public void 成行を明示的に否定すると現在値の指値になる()
    {
        var outcome = Create(LedgerWithLong()).Request(Command(marketOrder: false), Actor);

        outcome.Accepted.Should().BeTrue();
        outcome.Approval!.Intent.MarketOrder.Should().BeFalse();
        outcome.Approval.Intent.Price.Should().Be(333.59m);
    }

    // 指値を明示的に否定した（= 指値要求）のに現在値が無い場合は従来どおり拒否する（価格 0 の指値を投げない）。
    [Fact]
    public void 指値を選んだのに価格が決まらなければ拒否する()
    {
        Create(LedgerWithLong(), prices: new FakeCurrentPriceSource())
            .Request(Command(marketOrder: false), Actor)
            .Rejection.Should().Be(PositionCloseRejection.PriceUnavailable);
    }

    // 黙ってどちらかを捨てない（成行と指値は両立しない）。
    [Fact]
    public void 成行と指値の同時指定は拒否する()
    {
        Create(LedgerWithLong()).Request(Command(limitPrice: 335m, marketOrder: true), Actor)
            .Rejection.Should().Be(PositionCloseRejection.ConflictingPriceMode);
    }

    // 成行にしても在庫ガードは 1 バイトも緩まない（建玉を超える成行は作れない）。
    [Fact]
    public void 成行でも建玉を超える数量は拒否する()
    {
        var ledger = LedgerWithLong(quantity: 10);

        Create(ledger).Request(new PositionCloseCommand(
                "SOXL", Market.UnitedStates, 11, null, "手仕舞い", true), Actor)
            .Rejection.Should().Be(PositionCloseRejection.ExceedsAvailable);
    }
}
