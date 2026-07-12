using System.Collections.Concurrent;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Application.State;
using AiStockTrading.CostControl.Domain;
using FluentAssertions;
using Xunit;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Application.Tests;

// NFR（費用）, IADR-0027: 費用計上・累計・上方遷移検知・月跨ぎ・費用レビューを検証する。
public class CostControlServiceTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; } = now; }
    private sealed class Limits(MonthlyCostLimits limits) : ICostLimitsProvider { public MonthlyCostLimits GetLimits() => limits; }

    private static readonly MonthlyCostLimits Standard = new(20_000m, 15_000m, 5_000m, 0m);

    private static AppSvc NewService(DateTimeOffset now, out InMemoryCostLedger ledger, MonthlyCostLimits? limits = null)
    {
        ledger = new InMemoryCostLedger();
        return new AppSvc(ledger, new Limits(limits ?? Standard), new FixedClock(now));
    }

    [Fact]
    public void 費用は月内に累計され状態が上方遷移でしきい値を返す()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);

        // 累計 12,000（=80%）でしきい値 Throttled へ上方遷移。
        svc.Record(CostCategory.Llm, 11_000m).CrossedTo.Should().BeNull();      // <80%
        var crossed = svc.Record(CostCategory.Llm, 1_000m);                     // 合計12,000=80%
        crossed.CrossedTo.Should().Be(CostControlState.Throttled);
        crossed.Percent.Should().Be(80m);

        // 同一状態内の追加では発行しない。
        svc.Record(CostCategory.Llm, 500m).CrossedTo.Should().BeNull();
    }

    [Fact]
    public void 百パーセントで_Halted_へ遷移する()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);
        svc.Record(CostCategory.Llm, 12_000m); // Throttled

        var halted = svc.Record(CostCategory.Llm, 3_000m); // 合計15,000=100%

        halted.CrossedTo.Should().Be(CostControlState.Halted);
        halted.Decision.IsHalted.Should().BeTrue();
    }

    [Fact]
    public void 月をまたぐと累計はリセットされる()
    {
        var ledger = new InMemoryCostLedger();
        var july = new AppSvc(ledger, new Limits(Standard), new FixedClock(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));
        july.Record(CostCategory.Llm, 15_000m); // 7月は Halted
        july.GetLlmState().State.Should().Be(CostControlState.Halted);

        // 8月は別月のため 0 から（Normal）。
        var august = new AppSvc(ledger, new Limits(Standard), new FixedClock(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
        august.GetLlmState().State.Should().Be(CostControlState.Normal);
    }

    [Fact]
    public void 非LLMカテゴリは統制状態に影響しない()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);

        svc.Record(CostCategory.Infrastructure, 5_000m).CrossedTo.Should().BeNull();
        svc.GetLlmState().State.Should().Be(CostControlState.Normal);
    }

    [Fact]
    public async Task 並行計上でもしきい値遷移は各しきい値ちょうど1回()
    {
        // NFR（費用）, IADR-0034: 並行計上で before/after が原子化され、CostThresholdReached の重複/取りこぼしが起きない。
        // 300 × 50 = 15,000 → 80%(12,000)・100%(15,000)を跨ぐ。順序に依らず各しきい値の上方遷移は 1 回のみ。
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);
        var results = new ConcurrentBag<RecordCostResult>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 50), async (_, _) =>
        {
            await Task.Yield();
            results.Add(svc.Record(CostCategory.Llm, 300m));
        });

        results.Should().HaveCount(50);
        results.Count(r => r.CrossedTo == CostControlState.Throttled).Should().Be(1);
        results.Count(r => r.CrossedTo == CostControlState.Halted).Should().Be(1);
    }

    [Fact]
    public void 費用レビューは全カテゴリ累計対資金を返す()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);
        svc.Record(CostCategory.Llm, 1_000m);
        svc.Record(CostCategory.Infrastructure, 1_000m);

        svc.Review(100_000m).Should().Be(0.02m); // 2,000 / 100,000
    }
}
