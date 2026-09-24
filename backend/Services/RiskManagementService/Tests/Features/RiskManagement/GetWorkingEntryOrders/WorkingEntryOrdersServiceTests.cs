using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-04, FR-10, ADR-0003, #934, IADR-0390 決定1: 取引判断へ渡す「当日の未約定の新規建て注文」を、統制（IADR-0346）と
// 同じ 2 射影（承認＝台帳・生死＝注文アクティビティ）から導くことを、本番のハンドラチェーンと同じ書き込みで検証する。
public class WorkingEntryOrdersServiceTests
{
    // 2026-09-23 13:51:48 UTC（実測の 2 本目の判断時刻＝22:51:48 JST）。ET では 9/23 の取引日。
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 13, 51, 48, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
    }

    private static OrderIntent Intent(int qty, decimal price, PositionEffect effect = PositionEffect.Open, string symbol = "AAPL") =>
        new(symbol, Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, qty, price, effect,
            StopLossPrice: null, FxRateToBase: 1m);

    // T-10-720 / T-10-721（サービス）: 実測の形——1 本目（指値 715 株）は受理済みで未約定のまま。終端した注文・決済・前取引日の
    // 未終端・全量約定済みは出ない。部分約定は残数量だけが出る（約定分は /open-positions の建玉側）。
    [Fact]
    public void 当日の未終端の新規建てだけを残数量つきで返し終端と決済と前取引日と全量約定は返さない()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var activity = new InMemoryOrderActivityStore();
        var service = new WorkingEntryOrdersService(ledger, new InMemoryWorkingEntryOrderSource(ledger, activity), new FixedClock());

        void Approve(Guid id, OrderIntent intent, DateTimeOffset at)
        {
            ledger.AppendApproval(id, intent, at);
            activity.RecordPlacement(id, intent.Symbol, intent.Market, intent.Side, intent.Quantity, at);
        }

        var resting = Guid.NewGuid();
        Approve(resting, Intent(715, 337.63m), Now.AddMinutes(-5));
        activity.RecordExecution(resting, OrderStatus.Accepted, 0, Now.AddMinutes(-5).AddSeconds(1));

        var partial = Guid.NewGuid();
        Approve(partial, Intent(100, 338m, symbol: "MSFT"), Now.AddMinutes(-4));
        ledger.AppendFill(partial, "o-partial", 40, 338m, Now.AddMinutes(-3));
        activity.RecordExecution(partial, OrderStatus.PartiallyFilled, 40, Now.AddMinutes(-3));

        var cancelled = Guid.NewGuid();
        Approve(cancelled, Intent(10, 300m), Now.AddMinutes(-4));
        activity.RecordCancellation(cancelled, Now.AddMinutes(-2));

        var forgone = Guid.NewGuid();
        Approve(forgone, Intent(10, 300m), Now.AddMinutes(-4));
        activity.RecordForgone(forgone, "AAPL", Market.UnitedStates, TradeSide.Buy, 10, Now.AddMinutes(-4));

        Approve(Guid.NewGuid(), Intent(10, 300m, PositionEffect.Close), Now.AddMinutes(-4));
        Approve(Guid.NewGuid(), Intent(10, 300m), Now.AddDays(-1));

        var filledNotYetTerminal = Guid.NewGuid();
        Approve(filledNotYetTerminal, Intent(20, 300m), Now.AddMinutes(-4));
        ledger.AppendFill(filledNotYetTerminal, "o-full", 20, 300m, Now.AddMinutes(-3));

        var result = service.Build();

        result.Should().BeEquivalentTo(new[]
        {
            new WorkingEntryOrderView(resting, "AAPL", Market.UnitedStates, TradeSide.Buy, 715, 337.63m, Now.AddMinutes(-5)),
            new WorkingEntryOrderView(partial, "MSFT", Market.UnitedStates, TradeSide.Buy, 60, 338m, Now.AddMinutes(-4)),
        });
    }

    [Fact]
    public void 未約定が無ければ空を返す()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var service = new WorkingEntryOrdersService(
            ledger, new InMemoryWorkingEntryOrderSource(ledger, new InMemoryOrderActivityStore()), new FixedClock());

        service.Build().Should().BeEmpty();
    }
}
