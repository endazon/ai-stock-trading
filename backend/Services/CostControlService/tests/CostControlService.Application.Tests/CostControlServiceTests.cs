using System.Collections.Concurrent;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Application.State;
using AiStockTrading.CostControl.Domain;
using AwesomeAssertions;
using Xunit;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Application.Tests;

// NFR（費用）, IADR-0027: 費用計上・累計・上方遷移検知・月跨ぎ・費用レビューを検証する。
// #139, IADR-0065: 上限は供給元（設定サービス）で変わりうるため、変更がしきい値判定に効くことも併せて固定する。
public class CostControlServiceTests
{
    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow { get; } = now; }

    // FR-17, #139: 上限は利用者が設定サービスで変更しうる。変更をこの場で再現できるよう可変にする。
    private sealed class Limits(MonthlyCostLimits limits) : ICostLimitsProvider
    {
        public MonthlyCostLimits Current { get; set; } = limits;

        public ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
    }

    private static readonly MonthlyCostLimits Standard = new(20_000m, 15_000m, 5_000m, 0m);

    private static AppSvc NewService(DateTimeOffset now, out InMemoryCostLedger ledger, MonthlyCostLimits? limits = null)
    {
        ledger = new InMemoryCostLedger();
        return new AppSvc(ledger, new Limits(limits ?? Standard), new FixedClock(now));
    }

    [Fact]
    public async Task 費用は月内に累計され状態が上方遷移でしきい値を返す()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);

        // 累計 12,000（=80%）でしきい値 Throttled へ上方遷移。
        (await svc.RecordAsync(CostCategory.Llm, 11_000m)).CrossedTo.Should().BeNull();      // <80%
        var crossed = await svc.RecordAsync(CostCategory.Llm, 1_000m);                       // 合計12,000=80%
        crossed.CrossedTo.Should().Be(CostControlState.Throttled);
        crossed.Percent.Should().Be(80m);

        // 同一状態内の追加では発行しない。
        (await svc.RecordAsync(CostCategory.Llm, 500m)).CrossedTo.Should().BeNull();
    }

    [Fact]
    public async Task 百パーセントで_Halted_へ遷移する()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);
        await svc.RecordAsync(CostCategory.Llm, 12_000m); // Throttled

        var halted = await svc.RecordAsync(CostCategory.Llm, 3_000m); // 合計15,000=100%

        halted.CrossedTo.Should().Be(CostControlState.Halted);
        halted.Decision.IsHalted.Should().BeTrue();
    }

    [Fact]
    public async Task 月をまたぐと累計はリセットされる()
    {
        var ledger = new InMemoryCostLedger();
        var july = new AppSvc(ledger, new Limits(Standard), new FixedClock(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero)));
        await july.RecordAsync(CostCategory.Llm, 15_000m); // 7月は Halted
        (await july.GetLlmStateAsync()).State.Should().Be(CostControlState.Halted);

        // 8月は別月のため 0 から（Normal）。
        var august = new AppSvc(ledger, new Limits(Standard), new FixedClock(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
        (await august.GetLlmStateAsync()).State.Should().Be(CostControlState.Normal);
    }

    [Fact]
    public async Task 非LLMカテゴリは統制状態に影響しない()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);

        (await svc.RecordAsync(CostCategory.Infrastructure, 5_000m)).CrossedTo.Should().BeNull();
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Normal);
    }

    // FR-17, #139, IADR-0065: 利用者が設定サービスで上限を絞ると、同じ累計でも統制状態が上がる。
    [Fact]
    public async Task 上限が絞られるとしきい値判定に反映される()
    {
        var limits = new Limits(Standard);
        var svc = new AppSvc(new InMemoryCostLedger(), limits, new FixedClock(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero)));

        // 上限 15,000 に対し 3,000（20%）＝ Normal。
        await svc.RecordAsync(CostCategory.Llm, 3_000m);
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Normal);

        // 利用者が LLM 上限を 15,000 → 3,000 へ変更。同じ累計 3,000 が 100% になり停止へ。
        limits.Current = Standard with { Llm = 3_000m };

        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Halted);
    }

    // 上限が緩められた場合も追随する（片方向でないことを固定する）。
    [Fact]
    public async Task 上限が緩められるとしきい値判定に反映される()
    {
        var limits = new Limits(Standard with { Llm = 3_000m });
        var svc = new AppSvc(new InMemoryCostLedger(), limits, new FixedClock(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero)));

        await svc.RecordAsync(CostCategory.Llm, 3_000m); // 上限 3,000 に対し 100% ＝ Halted
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Halted);

        limits.Current = Standard; // 上限 15,000 へ戻すと 20% ＝ Normal

        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Normal);
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
            results.Add(await svc.RecordAsync(CostCategory.Llm, 300m));
        });

        results.Should().HaveCount(50);
        results.Count(r => r.CrossedTo == CostControlState.Throttled).Should().Be(1);
        results.Count(r => r.CrossedTo == CostControlState.Halted).Should().Be(1);
    }

    [Fact]
    public async Task 費用レビューは全カテゴリ累計対資金を返す()
    {
        var svc = NewService(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), out _);
        await svc.RecordAsync(CostCategory.Llm, 1_000m);
        await svc.RecordAsync(CostCategory.Infrastructure, 1_000m);

        svc.Review(100_000m).Should().Be(0.02m); // 2,000 / 100,000
    }
}
