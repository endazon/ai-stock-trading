using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-11, FR-16, UC-07, ADR-0016 決定15, ADR-0027 決定2/決定4, #633, IADR-0300:
// 取引の経費区分の記録（段 1＝「取れないことを記録する」経路）。
//
// issue #633 の受け入れ基準のうち本作業が担う範囲を固定する。**否定形が本体である** ——
// 「区分が取れない費用を既存区分へ丸めない」「明細 0 件の建玉は 7 区分を LineCount = 0 で返す」。
public class TradeExpenseRecordingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 6, 30, 0, TimeSpan.Zero);

    // 明細を供給するポート（段 2 の実装の代わり）。
    private sealed class SuppliedSource(params TradeExpense[] lines) : IOrderExpenseSource
    {
        public int CallCount { get; private set; }

        public OrderExpenseQuery? LastQuery { get; private set; }

        public Task<OrderExpenseLookup> GetOrderExpensesAsync(
            OrderExpenseQuery query, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastQuery = query;
            return Task.FromResult(OrderExpenseLookup.Supplied(lines));
        }
    }

    // 照会が例外で落ちるポート（外部依存の障害）。
    private sealed class ThrowingSource : IOrderExpenseSource
    {
        public Task<OrderExpenseLookup> GetOrderExpensesAsync(
            OrderExpenseQuery query, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("照会がタイムアウトした。");
    }

    // 照会したかどうかを数える既定（常に取得できない）。
    private sealed class CountingUnsuppliedSource : IOrderExpenseSource
    {
        public int CallCount { get; private set; }

        public Task<OrderExpenseLookup> GetOrderExpensesAsync(
            OrderExpenseQuery query, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return new UnsuppliedOrderExpenseSource().GetOrderExpensesAsync(query, cancellationToken);
        }
    }

    private static ExecutionRecord Filled(Guid decisionId) =>
        new(decisionId, "ORD-1", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash,
            PositionEffect.Open, 10, 340m, 10, 340.5m, OrderStatus.Filled, 0.0015m, Now);

    private static OrderExecuted Executed(Guid decisionId, int filledQuantity = 10) =>
        new(decisionId, "ORD-1", OrderStatus.Filled, filledQuantity, 340.5m, Now, BrokerProvider.InternalPaper);

    private static TradeExpense Line(TradeExpenseCategory category, decimal amountUsd) =>
        new("AAPL", Market.UnitedStates, category, amountUsd, new DateOnly(2026, 9, 4), "ORD-1", Now);

    private static (TradeExpenseRecordingService Service, InMemoryExecutedOrderStore Store) Build(
        IOrderExpenseSource source, Guid decisionId)
    {
        var store = new InMemoryExecutedOrderStore();
        store.Save(Filled(decisionId));
        return (new TradeExpenseRecordingService(source, store), store);
    }

    // 🔴 issue #633 の否定形そのもの: 明細が 1 件も取れなかった建玉は、7 区分すべてを LineCount = 0 で返す。
    // 「0 円だった（費用が発生しなかった）」ではなく「1 件も計上されていない」ことが読める形にする。
    [Fact]
    public async Task 未供給では発行イベントが無く7区分すべてが未計上になる()
    {
        var decisionId = Guid.NewGuid();
        var (service, _) = Build(new UnsuppliedOrderExpenseSource(), decisionId);

        var outcome = await service.RecordForExecutionAsync(Executed(decisionId));

        outcome.IsUnavailable.Should().BeTrue();
        outcome.IsSkipped.Should().BeFalse();
        outcome.Events.Should().BeEmpty();
        outcome.UnavailableReason.Should().Be(UnsuppliedOrderExpenseSource.Reason);

        var summary = outcome.Summary.Should().NotBeNull().And.Subject.As<PositionExpenseSummary>();
        summary.Symbol.Should().Be("AAPL");
        summary.Market.Should().Be(Market.UnitedStates);
        summary.Totals.Should().HaveCount(TradeExpenseClassification.All.Count);
        summary.Totals.Should().OnlyContain(t => t.LineCount == 0 && t.AmountUsd == 0m);
        summary.Totals.Should().OnlyContain(t => !t.HasLines);
    }

    // 🔴 否定形: 区分が取得できない費用を Commission などの既存区分へ**丸めない**。
    // 丸めが起きていれば、いずれかの区分に件数か金額が立つ。
    [Fact]
    public async Task 未供給の費用を既存区分へ丸めない()
    {
        var decisionId = Guid.NewGuid();
        var (service, _) = Build(new UnsuppliedOrderExpenseSource(), decisionId);

        var outcome = await service.RecordForExecutionAsync(Executed(decisionId));
        var summary = outcome.Summary!;

        foreach (var category in TradeExpenseClassification.All)
        {
            summary.For(category).LineCount.Should().Be(0, $"区分 {category} は推定してはならない");
            summary.For(category).AmountUsd.Should().Be(0m);
        }

        summary.TotalExpensesUsd.Should().Be(0m);
        summary.RealizedUsd.Should().Be(0m);
    }

    // ADR-0027 決定2: 建玉の一次識別子は (銘柄, 市場)。供給された明細は建玉単位で集計できる。
    [Fact]
    public async Task 供給された明細は1行につき1本発行され建玉単位で集計できる()
    {
        var decisionId = Guid.NewGuid();
        var source = new SuppliedSource(
            Line(TradeExpenseCategory.Commission, 1.5m),
            Line(TradeExpenseCategory.Fee, 0.25m),
            Line(TradeExpenseCategory.FxCost, 0.75m));
        var (service, _) = Build(source, decisionId);

        var outcome = await service.RecordForExecutionAsync(Executed(decisionId));

        outcome.IsUnavailable.Should().BeFalse();
        outcome.Events.Should().HaveCount(3);
        outcome.Events.Should().OnlyContain(e =>
            e.Expense.Symbol == "AAPL" && e.Expense.Market == Market.UnitedStates);

        // 建玉の一次識別子で照会できることを、契約側の集計関数そのもので確かめる。
        var byPosition = TradeExpenseLedger.SummarizeByPosition(
            [.. outcome.Events.Select(e => e.Expense)]);
        var position = byPosition.Should().ContainSingle().Which;
        position.Symbol.Should().Be("AAPL");
        position.For(TradeExpenseCategory.Commission).LineCount.Should().Be(1);
        position.For(TradeExpenseCategory.MarginInterest).LineCount.Should().Be(0);
        position.TotalExpensesUsd.Should().Be(2.5m);

        // 照会は約定した注文 1 件を指す（建玉の識別子は発注記録から取る）。
        source.LastQuery!.Symbol.Should().Be("AAPL");
        source.LastQuery.Market.Should().Be(Market.UnitedStates);
        source.LastQuery.OrderId.Should().Be("ORD-1");
        source.LastQuery.DecisionId.Should().Be(decisionId);
    }

    // 約定していない注文に経費は発生していない。照会そのものを行わない。
    [Fact]
    public async Task 約定していない注文は照会しない()
    {
        var decisionId = Guid.NewGuid();
        var source = new CountingUnsuppliedSource();
        var (service, _) = Build(source, decisionId);

        var outcome = await service.RecordForExecutionAsync(Executed(decisionId, filledQuantity: 0));

        outcome.IsSkipped.Should().BeTrue();
        outcome.Summary.Should().BeNull();
        outcome.Events.Should().BeEmpty();
        source.CallCount.Should().Be(0);
    }

    // fail-safe: 建玉 (銘柄, 市場) を特定できないなら推測しない（別の建玉の費用として 7 年残る）。
    [Fact]
    public async Task 発注記録が無ければ照会せず打ち切る()
    {
        var source = new CountingUnsuppliedSource();
        var service = new TradeExpenseRecordingService(source, new InMemoryExecutedOrderStore());

        var outcome = await service.RecordForExecutionAsync(Executed(Guid.NewGuid()));

        outcome.IsSkipped.Should().BeTrue();
        outcome.Summary.Should().BeNull();
        source.CallCount.Should().Be(0);
    }

    // fail-safe: 経費の記録が発注執行を止めてはならない。例外は「取得できない」へ倒す。
    [Fact]
    public async Task 照会が例外でも取得できないへ倒れる()
    {
        var decisionId = Guid.NewGuid();
        var (service, _) = Build(new ThrowingSource(), decisionId);

        var outcome = await service.RecordForExecutionAsync(Executed(decisionId));

        outcome.IsUnavailable.Should().BeTrue();
        outcome.UnavailableReason.Should().Contain(nameof(TimeoutException));
        outcome.Events.Should().BeEmpty();
        outcome.Summary!.Totals.Should().OnlyContain(t => t.LineCount == 0);
    }

    // 照会できて明細が 0 件（＝費用が発生しなかった）は、照会できなかったのとは**別の事実**である。
    [Fact]
    public async Task 照会できて明細0件は未供給と区別される()
    {
        var decisionId = Guid.NewGuid();
        var (service, _) = Build(new SuppliedSource(), decisionId);

        var outcome = await service.RecordForExecutionAsync(Executed(decisionId));

        outcome.IsUnavailable.Should().BeFalse();
        outcome.IsSkipped.Should().BeFalse();
        outcome.Events.Should().BeEmpty();
        outcome.Summary!.Totals.Should().OnlyContain(t => t.LineCount == 0);
    }
}
