using AiStockTrading.CostControl.Application.Adapters;
using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Application.Tests;

// NFR（費用）, FR-16, FR-17, 05_trading-assumptions §6・§6.1, #347, IADR-0218:
// **対象外カテゴリの分離**（否定形）と**月報への実績供給**、および月次リセット・上限変更の境界。
public class CostScopeAndUsageTests
{
    private sealed class MovableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Limits(MonthlyCostLimits limits) : ICostLimitsProvider
    {
        public MonthlyCostLimits Current { get; set; } = limits;

        public ValueTask<MonthlyCostLimits> GetLimitsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);
    }

    // 05_trading-assumptions §6 の初期配分（総額 20,000 / LLM 15,000 / インフラ 5,000 / データ 0）。
    private static readonly MonthlyCostLimits Standard = new(20_000m, 15_000m, 5_000m, 0m);

    // ---- 🔴 否定形: 対象外カテゴリは統制状態を動かさない ------------------------------------

    // 計画 §6 は「月次データ費用上限・インフラ費用上限・月次総費用は予算の目安であり、**自動統制の対象外**」と定める。
    // §6.1 は報告書生成・情報収集の LLM 費用も対象外とする。**どれを積んでも統制状態は Normal のままである。**
    [Theory]
    [InlineData(CostCategory.LlmUncapped)]
    [InlineData(CostCategory.Infrastructure)]
    [InlineData(CostCategory.Data)]
    public async Task 対象外カテゴリの計上は統制状態を動かさない(CostCategory category)
    {
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard), new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        // LLM 上限 15,000 円をはるかに超える額を積む。
        var result = await svc.RecordAsync(category, 1_000_000m);

        result.CrossedTo.Should().BeNull();
        result.Decision.State.Should().Be(CostControlState.Normal);
        result.Decision.IsHalted.Should().BeFalse();
        result.Percent.Should().Be(0m);

        // 照会経路（定時サイクルが引く /costs/state 相当）も Normal のままで、間隔延長すら起きない。
        var state = await svc.GetLlmStateAsync();
        state.State.Should().Be(CostControlState.Normal);
        state.IntervalMultiplier.Should().Be(1m);
    }

    // 🔴 **「取引停止・報告書生成停止に波及しない」の機械的な表明**（#347 の受け入れ基準）。
    // 上限側が Halted になっても、対象外カテゴリの計上はそのまま受け付けられ、
    // **報告書生成の費用計上が拒否・停止されることはない**（費用統制は計上を止める機構ではない）。
    [Fact]
    public async Task 上限到達後も対象外の費用は計上でき_報告書生成へ波及しない()
    {
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard), new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        // 取引判断の費用で 100%（15,000 円）へ到達させる＝定時サイクルは停止する。
        var halted = await svc.RecordAsync(CostCategory.Llm, 15_000m);
        halted.CrossedTo.Should().Be(CostControlState.Halted);

        // その後も報告書生成の費用は計上できる（例外にならない・記録が残る）。
        var afterHalt = await svc.RecordAsync(CostCategory.LlmUncapped, 800m);
        afterHalt.Decision.State.Should().Be(CostControlState.Halted); // 統制状態は上限側の事実を映すだけ
        ledger.GetMonthlyTotal("2026-08", CostCategory.LlmUncapped).Should().Be(800m);

        // 上限側の累計は対象外の計上で 1 円も動かない。
        ledger.GetMonthlyTotal("2026-08", CostCategory.Llm).Should().Be(15_000m);
    }

    // ---- 境界値: しきい値の同値・直前・直後 ------------------------------------------------

    [Theory]
    // 上限 15,000 円に対する累計と、期待する統制状態（80%＝12,000 / 100%＝15,000）。
    [InlineData(11_999.99, CostControlState.Normal)]
    [InlineData(12_000, CostControlState.Throttled)]   // 同値は到達（>= 判定）
    [InlineData(12_000.01, CostControlState.Throttled)]
    [InlineData(14_999.99, CostControlState.Throttled)]
    [InlineData(15_000, CostControlState.Halted)]      // 同値は到達
    [InlineData(15_000.01, CostControlState.Halted)]
    public async Task しきい値の境界は同値で到達する(decimal amount, CostControlState expected)
    {
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard), new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        (await svc.RecordAsync(CostCategory.Llm, amount)).Decision.State.Should().Be(expected);
    }

    // ---- 月次リセットの境界 -----------------------------------------------------------------

    // 月が変われば累計はゼロから始まる（統制も解除される）。月末最終秒→翌月最初の秒の境界で確かめる。
    [Fact]
    public async Task 月をまたぐと累計はリセットされ統制も解除される()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero));
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard), clock);

        (await svc.RecordAsync(CostCategory.Llm, 15_000m)).Decision.State.Should().Be(CostControlState.Halted);

        clock.UtcNow = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Normal);
        (await svc.RecordAsync(CostCategory.Llm, 1m)).Decision.State.Should().Be(CostControlState.Normal);

        // 前月の記録は消えない（7 年保持の台帳・月報の遡及集計に要る）。
        ledger.GetMonthlyTotal("2026-08", CostCategory.Llm).Should().Be(15_000m);
        ledger.GetMonthlyTotal("2026-09", CostCategory.Llm).Should().Be(1m);
    }

    // ---- 上限変更（FR-17・前提条件バージョン切替）の境界 ------------------------------------

    // 同じ累計でも、上限が下がれば統制は効き、戻せば解除される（供給元は都度参照される）。
    [Fact]
    public async Task 上限の変更は同じ累計に対して即座に効く()
    {
        var ledger = new InMemoryCostLedger();
        var limits = new Limits(Standard);
        var svc = new AppSvc(ledger, limits, new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        (await svc.RecordAsync(CostCategory.Llm, 6_000m)).Decision.State.Should().Be(CostControlState.Normal);

        // 前提条件 v2: LLM 上限を 15,000 → 6,000 円へ引き下げる（累計は 6,000 のまま＝100%）。
        limits.Current = Standard with { Llm = 6_000m };
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Halted);

        // v3: 引き上げれば解除される（累計 6,000 は 7,500 の 80%）。
        limits.Current = Standard with { Llm = 7_500m };
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Throttled);

        limits.Current = Standard;
        (await svc.GetLlmStateAsync()).State.Should().Be(CostControlState.Normal);
    }

    // 上限 0（未設定）は統制しない＝ゼロ除算も起こさない（fail-safe の据え置き）。
    [Fact]
    public async Task 上限が未設定なら統制しない()
    {
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard with { Llm = 0m }),
            new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        var result = await svc.RecordAsync(CostCategory.Llm, 999_999m);

        result.Decision.State.Should().Be(CostControlState.Normal);
        result.Percent.Should().Be(0m);
    }

    // ---- 月報への実績供給 -------------------------------------------------------------------

    // §6.1: 対象外の費用も**月報に実績を記載する**（#282 の過少申告の再発防止）。
    // 供給形は「カテゴリ別の内訳 ＋ 対象分だけの消費率」である。
    [Fact]
    public async Task 月報へはカテゴリ別の内訳と対象分の消費率を供給する()
    {
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard), new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        await svc.RecordAsync(CostCategory.Llm, 3_000m);           // 取引判断（対象）
        await svc.RecordAsync(CostCategory.LlmUncapped, 1_200m);   // 報告書生成・情報収集（対象外）
        await svc.RecordAsync(CostCategory.Infrastructure, 4_000m);

        var usage = await svc.GetUsageAsync();

        usage.Month.Should().Be("2026-08");
        usage.LlmLimit.Should().Be(15_000m);
        usage.Totals[CostCategory.Llm].Should().Be(3_000m);
        usage.Totals[CostCategory.LlmUncapped].Should().Be(1_200m);
        usage.Totals[CostCategory.Infrastructure].Should().Be(4_000m);

        // 🔴 消費率の分子は**対象分だけ**（対象外を混ぜると「上限に近づいている」という誤ったシグナルになる）。
        usage.GovernedPercent.Should().Be(20m);
    }

    // 計上のない月は内訳が空で返る（0 円の行を作らない）。
    [Fact]
    public async Task 計上のない月の内訳は空である()
    {
        var ledger = new InMemoryCostLedger();
        var svc = new AppSvc(ledger, new Limits(Standard), new MovableClock(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)));

        var usage = await svc.GetUsageAsync();

        usage.Totals.Should().BeEmpty();
        usage.GovernedPercent.Should().Be(0m);
    }
}
