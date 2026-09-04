using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-11, ADR-0016 決定15, ADR-0027 決定4, #633, IADR-0300:
// 経費明細の供給が無い構成の安全既定。**「取得できない」と「1 件も無かった」を型で分ける。**
public class UnsuppliedOrderExpenseSourceTests
{
    private static OrderExpenseQuery Query() =>
        new(Guid.NewGuid(), "ORD-1", "AAPL", Market.UnitedStates,
            new DateTimeOffset(2026, 9, 4, 6, 30, 0, TimeSpan.Zero));

    // 🔴 否定形: 空の明細（＝照会できて費用 0）を返さない。空を返すと、供給の結線を忘れた期間が
    // そのまま「費用なし」で通る。
    [Fact]
    public async Task 既定実装は常に取得できないを返す()
    {
        var lookup = await new UnsuppliedOrderExpenseSource().GetOrderExpensesAsync(Query());

        lookup.IsSupplied.Should().BeFalse();
        lookup.UnavailableReason.Should().Be(UnsuppliedOrderExpenseSource.Reason);
    }

    // 型の上で「未供給を 0 件として合計へ混ぜる」経路が無いことを固定する（IADR-0183 と同じ規律）。
    [Fact]
    public async Task 未供給の結果から明細を読むと例外になる()
    {
        var lookup = await new UnsuppliedOrderExpenseSource().GetOrderExpensesAsync(Query());

        var read = () => lookup.Lines;

        read.Should().Throw<InvalidOperationException>();
    }

    // 逆向きも塞ぐ: 照会できた結果に「取得できなかった理由」は無い。
    [Fact]
    public void 供給された結果から理由を読むと例外になる()
    {
        var lookup = OrderExpenseLookup.Supplied([]);

        var read = () => lookup.UnavailableReason;

        lookup.IsSupplied.Should().BeTrue();
        lookup.Lines.Should().BeEmpty();
        read.Should().Throw<InvalidOperationException>();
    }

    // 理由の無い「取得できない」は作れない（診断できない未供給を残さない）。
    [Fact]
    public void 理由なしで取得できないは作れない()
    {
        var create = () => OrderExpenseLookup.Unavailable("  ");

        create.Should().Throw<ArgumentException>();
    }
}
