using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Application.Adapters;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.CostControl.Application.Tests;

// NFR（費用）, 05_trading-assumptions §6, IADR-0027/0065 決定 5: 設定サービスに依存しない既定の上限供給。
// 費用統制の fail-safe の**底**にあたる値であり、利用者決定（2026-07-07）の既定 20,000/15,000 から
// 無自覚にずれないよう固定する。
public class DefaultCostLimitsProviderTests
{
    [Fact]
    public async Task 前提条件の既定の上限を返す()
    {
        var limits = await new DefaultCostLimitsProvider().GetLimitsAsync();

        limits.Should().Be(TradingAssumptionsDefaults.Create().CostLimits);
        limits.Total.Should().Be(20_000m);
        limits.Llm.Should().Be(15_000m);
    }

    // 外部接続を伴わないため、いつ・何度呼んでも同じ値が即座に返る（同期完了）。
    [Fact]
    public void 外部接続なしで同期的に完了する()
    {
        var pending = new DefaultCostLimitsProvider().GetLimitsAsync();

        pending.IsCompleted.Should().BeTrue();
    }
}
