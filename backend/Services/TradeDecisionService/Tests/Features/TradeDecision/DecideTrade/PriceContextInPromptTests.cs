extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-02, FR-04, UC-01, ADR-0044 決定1, ADR-0020, ADR-0003, #1035, IADR-0451: 定時の判断に各銘柄の直近の値動き
// （前日比・当日始値比・日中高安）を材料として渡し、出来高は「未提供」と明示する。
// 実測（2026-09-26 稼働 PoC）: 定時の判断へ渡る市況は現在値 1 行だけで、一次・本判断とも「値動きの情報が無い」として全件 Hold に倒れた。
// 🔴 値が無ければ「不明」と書き 0 を書かない。変化率はコードが計算する（LLM に計算させない）。実 LLM は呼ばない。
public class PriceContextInPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 29), "AAPL は押し目で新規買いを検討する。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    // 現在値 102・前日終値 100・始値 103・高値 104・安値 99.5 → 前日比 +2.00%・当日始値比 -0.97%（(102-103)/103=-0.9708…）。
    private static readonly IntradayPriceContext Known = new(100m, 103m, 104m, 99.5m);

    private const string KnownPreviousLine = "- 前日終値: 100 / 前日比: +2.00%";
    private const string KnownOpenLine = "- 当日始値: 103 / 当日始値比: -0.97%";
    private const string KnownRangeLine = "- 日中高値: 104 / 日中安値: 99.5";

    private static DecisionTrigger ScheduledAapl() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    // ================================================================================================
    // 計算（コードで行う）
    // ================================================================================================

    [Theory]
    [InlineData(102, 100, 0.02)]
    [InlineData(98, 100, -0.02)]
    [InlineData(100, 100, 0)]
    public void 変化率は現在値と基準の差を基準で割った値(decimal price, decimal basis, decimal expected)
    {
        IntradayPriceContext.ChangeRatio(price, basis).Should().Be(expected);
    }

    // 🔴 否定形: 基準が不明・0 以下なら変化率も不明（0 を基準にすると -100% や 0 除算になる）。
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void 基準が不明または0以下なら変化率は不明(int? basis)
    {
        IntradayPriceContext.ChangeRatio(102m, basis).Should().BeNull();
    }

    [Fact]
    public void Ofは0以下の項目を不明へ倒す()
    {
        IntradayPriceContext.Of(100m, 0m, -1m, null).Should().Be(new IntradayPriceContext(100m, null, null, null));
    }

    // ================================================================================================
    // 値動きの行
    // ================================================================================================

    [Fact]
    public void 値動きの行は前日比と当日始値比と日中高安を符号つきで出す()
    {
        var lines = TradeDecisionPromptBuilder.PriceContextLines(102m, Known, priceUnit: string.Empty);

        lines.Should().Contain(KnownPreviousLine);
        lines.Should().Contain(KnownOpenLine);
        lines.Should().Contain(KnownRangeLine);
        lines.Should().Contain(TradeDecisionPromptBuilder.PriceContextComputedNote);
    }

    // 🔴 否定形: 日中文脈が無い・0 の項目は「不明」と書き、0 や 0.00% を書かない（「変化なし」と読まれる）。
    [Fact]
    public void 値が無い項目は不明と書き0を書かない_否定形()
    {
        var unknown = TradeDecisionPromptBuilder.PriceContextLines(102m, intraday: null, priceUnit: string.Empty);
        var zeros = TradeDecisionPromptBuilder.PriceContextLines(102m, new IntradayPriceContext(0m, 0m, 0m, 0m), string.Empty);

        foreach (var lines in new[] { unknown, zeros })
        {
            lines.Should().Contain("- 前日終値: 不明 / 前日比: 不明");
            lines.Should().Contain("- 当日始値: 不明 / 当日始値比: 不明");
            lines.Should().Contain("- 日中高値: 不明 / 日中安値: 不明");
            lines.Should().NotContain("0.00%");
            lines.Should().NotContain("-100");
            lines.Should().NotContain(": 0 ");
        }
    }

    // 出来高は、判断の出来高が無効（既定。ADR-0048 決定 3 の確認が済むまで）の構成では、無言で省かず「未提供」と明示する（ADR-0020）。
    // ［#1118・IADR-0467］有効化した構成の行は DailyVolumeInPromptTests（T-10-1833〜T-10-1835）。
    [Fact]
    public void 出来高は未提供と明示する()
    {
        TradeDecisionPromptBuilder.PriceContextLines(102m, Known, string.Empty)
            .Should().Contain($"- {TradeDecisionPromptBuilder.VolumeNotProvidedLine}");
        TradeDecisionPromptBuilder.PriceContextLines(102m, intraday: null, string.Empty)
            .Should().Contain($"- {TradeDecisionPromptBuilder.VolumeNotProvidedLine}");
    }

    // 縮退の見積り: 値動きの行の最悪長（桁の多い価格・通貨表記・全項目あり・負の変化率）が保護分の予約を超えない。
    [Fact]
    public void 値動きの行の最悪長は縮退の保護分の予約に収まる()
    {
        var longest = new IntradayPriceContext(1m, 1m, 999_999_999.999999m, 999_999_999.999999m);
        var lines = TradeDecisionPromptBuilder.PriceContextLines(999_999_999.999999m, longest, " JPY");

        lines.Length.Should().BeLessThanOrEqualTo(ScreeningContextAssembler.PriceContextReserveChars);
    }

    // ================================================================================================
    // プロンプト（本判断の定時・急変の節と一次）
    // ================================================================================================

    [Fact]
    public void 本判断の定時の節に値動きの行が出る()
    {
        var prompt = TradeDecisionPromptBuilder.Build(ScheduledAapl(), Policy, Context, currentPrice: 102m, intraday: Known);

        var section = Section(prompt, "# 定時サイクル（価格変動トリガーなし）");
        section.Should().Contain("- 現在値: 102");
        section.Should().Contain(KnownPreviousLine);
        section.Should().Contain(KnownOpenLine);
        section.Should().Contain(KnownRangeLine);
        section.Should().Contain(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
    }

    // 急変の節の変化率は、その節に出した現在値（トリガーの価格）から計算する。
    [Fact]
    public void 本判断の急変の節にもトリガーの価格から計算した値動きの行が出る()
    {
        var trigger = DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 105m, 100m, 0.05m, Now));

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, currentPrice: 102m, intraday: Known);

        var section = Section(prompt, "# 価格変動トリガー");
        section.Should().Contain("- 前日終値: 100 / 前日比: +5.00%");
        section.Should().Contain("- 当日始値: 103 / 当日始値比: +1.94%");
        section.Should().Contain(KnownRangeLine);
        section.Should().Contain(TradeDecisionPromptBuilder.VolumeNotProvidedLine);
    }

    [Fact]
    public void 一次スクリーニングにも同じ値動きの行が出る()
    {
        var prompt = TradeDecisionPromptBuilder.BuildScreening(ScheduledAapl(), Policy, Context, currentPrice: 102m, intraday: Known);

        prompt.Should().Contain(TradeDecisionPromptBuilder.PriceContextLines(102m, Known, string.Empty));
    }

    // 現在値を出さない構成（既定 NoOp）では比べる現在値が無いため値動きの行は出さない（IADR-0099 決定1 の現行動作）。
    [Fact]
    public void 定時で現在値が無ければ値動きの行は出さない()
    {
        var prompt = TradeDecisionPromptBuilder.Build(ScheduledAapl(), Policy, Context, currentPrice: null, intraday: Known);
        var screening = TradeDecisionPromptBuilder.BuildScreening(ScheduledAapl(), Policy, Context, currentPrice: null, intraday: Known);

        prompt.Should().NotContain("前日比");
        screening.Should().NotContain("前日比");
    }

    // ================================================================================================
    // 判断サービス（価格供給 → 一次・本判断のプロンプト）
    // ================================================================================================

    // budget: null＝縮退制御なし／既定 150,000 文字／500＝保護分すら収まらない（解消不能な超過）。
    // 🔴 縮退でも値動きの行（市況＝保護分）は削られない。
    [Theory]
    [InlineData(null)]
    [InlineData(DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars)]
    [InlineData(500)]
    public async Task 判断サービスは価格供給の日中文脈を一次と本判断の両方へ渡す(int? budget)
    {
        var llm = new RecordingLlm(
            """{"action":"Buy","rationale":"押し目","referencePrice":102,"stopLossDistancePerShare":2}""");
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: DecisionOrchestrationOptions.Default with { EnableScreening = true, ScreeningContextBudgetChars = budget },
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(102m, Known)));

        var decision = await service.DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull();
        llm.Prompts.Should().HaveCount(2, "一次＋本判断");
        llm.Prompts.Should().OnlyContain(p =>
            p.Contains(KnownPreviousLine) && p.Contains(KnownOpenLine) && p.Contains(KnownRangeLine)
            && p.Contains(TradeDecisionPromptBuilder.VolumeNotProvidedLine));
    }

    private static string Section(string prompt, string heading)
    {
        var start = prompt.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"見出し {heading} が無い");
        var end = prompt.IndexOf("\n#", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? prompt[start..] : prompt[start..end];
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class RecordingLlm(string output) : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(output);
        }
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context);
    }

    private sealed class FakeCurrentPrice(CurrentPriceReading? reading) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(reading);
    }
}
