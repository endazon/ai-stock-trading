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

// FR-04, FR-01, UC-01, UC-02, ADR-0020 決定2, #1081, IADR-0453: ニュースの欠測・未構成を RAG を経由せず取引判断のプロンプトへ明示する。
// 実測（2026-09-28 稼働 PoC）: rationale は「好材料ニュース等の情報が提供されていない」と書き、取れなかったのか・無かったのか・
// 集めていないのかが区別されなかった。🔴 4 状態（取得済み／欠測／未提供（未構成）／不明）のいずれかを必ず書く。実 LLM は呼ばない。
public class NewsStatusInPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 29), "AAPL は押し目で新規買いを検討する。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static DecisionTrigger ScheduledAapl() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    private static DecisionTrigger MovementAapl() => DecisionTrigger.FromPriceMovement(
        new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 105m, 100m, 0.05m, Now));

    // ================================================================================================
    // 行の文言（4 状態）
    // ================================================================================================

    [Theory]
    [InlineData(NewsCollectionStatus.Fetched, TradeDecisionPromptBuilder.NewsFetchedLine)]
    [InlineData(NewsCollectionStatus.Outage, TradeDecisionPromptBuilder.NewsOutageLine)]
    [InlineData(NewsCollectionStatus.NotConfigured, TradeDecisionPromptBuilder.NewsNotConfiguredLine)]
    public void 状態ごとに文言を書き分ける(NewsCollectionStatus status, string expected)
    {
        TradeDecisionPromptBuilder.NewsStatusLine(status).Should().Be($"- {expected}{Environment.NewLine}");
    }

    [Fact]
    public void 文言は状態の語で始まる()
    {
        TradeDecisionPromptBuilder.NewsFetchedLine.Should().StartWith("ニュース: 取得済み");
        TradeDecisionPromptBuilder.NewsOutageLine.Should().StartWith("ニュース: 欠測");
        TradeDecisionPromptBuilder.NewsNotConfiguredLine.Should().StartWith("ニュース: 未提供（");
        TradeDecisionPromptBuilder.NewsNotConfiguredLine.Should().Contain("構成されていない");
        TradeDecisionPromptBuilder.NewsUnknownLine.Should().StartWith("ニュース: 不明");
        // 欠測でも出口は止まっていないことを明示する（ADR-0020 決定2）。
        TradeDecisionPromptBuilder.NewsOutageLine.Should().Contain("手仕舞い・損切りは止まっていません");
    }

    // 🔴 null（未受信・期限切れ・旧イベント）と範囲外の値は「不明」。「取得済み」へ倒さない。
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(99)]
    public void nullと範囲外の値は不明(int? raw)
    {
        var status = raw is { } r ? (NewsCollectionStatus?)r : null;

        TradeDecisionPromptBuilder.NewsStatusLine(status)
            .Should().Be($"- {TradeDecisionPromptBuilder.NewsUnknownLine}{Environment.NewLine}");
    }

    // 縮退の見積り: 4 状態の最長が保護分の予約に収まる。
    [Fact]
    public void ニュースの行の最長は縮退の保護分の予約に収まる()
    {
        NewsCollectionStatus?[] all = [null, NewsCollectionStatus.Fetched, NewsCollectionStatus.Outage, NewsCollectionStatus.NotConfigured];

        all.Max(s => TradeDecisionPromptBuilder.NewsStatusLine(s).Length)
            .Should().BeLessThanOrEqualTo(ScreeningContextAssembler.NewsStatusReserveChars);
    }

    // ================================================================================================
    // プロンプト（本判断の定時・急変の節と一次。無条件）
    // ================================================================================================

    [Theory]
    [InlineData(null)]
    [InlineData(102)]
    public void 本判断の定時の節に現在値の有無に依らずニュースの行が出る(int? currentPrice)
    {
        var prompt = TradeDecisionPromptBuilder.Build(
            ScheduledAapl(), Policy, Context, currentPrice: currentPrice, news: NewsCollectionStatus.Outage);

        Section(prompt, "# 定時サイクル（価格変動トリガーなし）").Should().Contain(TradeDecisionPromptBuilder.NewsOutageLine);
    }

    [Fact]
    public void 本判断の急変の節にもニュースの行が出る()
    {
        var prompt = TradeDecisionPromptBuilder.Build(
            MovementAapl(), Policy, Context, news: NewsCollectionStatus.NotConfigured);

        Section(prompt, "# 価格変動トリガー").Should().Contain(TradeDecisionPromptBuilder.NewsNotConfiguredLine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(102)]
    public void 一次スクリーニングにも現在値の有無に依らずニュースの行が出る(int? currentPrice)
    {
        var prompt = TradeDecisionPromptBuilder.BuildScreening(
            ScheduledAapl(), Policy, Context, currentPrice: currentPrice, news: NewsCollectionStatus.Fetched);

        Section(prompt, "# 対象: AAPL").Should().Contain(TradeDecisionPromptBuilder.NewsFetchedLine);
    }

    // 🔴 無言で省かない: news を渡さない呼び出し（既定）でも「不明」と書く。
    [Fact]
    public void 状態を渡さなければ本判断も一次も不明と書く()
    {
        TradeDecisionPromptBuilder.Build(ScheduledAapl(), Policy, Context)
            .Should().Contain(TradeDecisionPromptBuilder.NewsUnknownLine);
        TradeDecisionPromptBuilder.BuildScreening(ScheduledAapl(), Policy, Context)
            .Should().Contain(TradeDecisionPromptBuilder.NewsUnknownLine);
    }

    // ================================================================================================
    // 判断サービス（ストア → 一次・本判断のプロンプト）
    // ================================================================================================

    // budget: null＝縮退制御なし／既定 150,000 文字／500＝保護分すら収まらない（解消不能な超過）。
    // 🔴 縮退でもニュースの行（欠測の明示＝保護分）は削られない。定時・急変の両方。
    [Theory]
    [InlineData(null, false)]
    [InlineData(DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars, false)]
    [InlineData(500, false)]
    [InlineData(null, true)]
    [InlineData(500, true)]
    public async Task 判断サービスは保持した状態を一次と本判断の両方へ渡す(int? budget, bool priceMovement)
    {
        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Outage, TimeSpan.FromMinutes(60), Now.AddMinutes(-10));
        var llm = new RecordingLlm();

        await NewService(llm, store, budget).DecideAsync(priceMovement ? MovementAapl() : ScheduledAapl());

        llm.Prompts.Should().HaveCount(2, "一次＋本判断");
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.NewsOutageLine));
    }

    // 🔴 期限切れは最後の値（欠測）ではなく「不明」を両方へ書く。
    [Fact]
    public async Task 期限切れの状態は一次と本判断の両方で不明と書く()
    {
        var store = new NewsCollectionStatusStore();
        store.Record(NewsCollectionStatus.Outage, TimeSpan.FromMinutes(30), Now.AddMinutes(-31));
        var llm = new RecordingLlm();

        await NewService(llm, store, budget: null).DecideAsync(ScheduledAapl());

        llm.Prompts.Should().HaveCount(2);
        llm.Prompts.Should().OnlyContain(p =>
            p.Contains(TradeDecisionPromptBuilder.NewsUnknownLine) && !p.Contains(TradeDecisionPromptBuilder.NewsOutageLine));
    }

    // 旧イベント（新項目 null）を受けた後も判断は動き、「不明」と書く（互換）。
    [Fact]
    public async Task 旧イベント由来の状態nullでも判断は動き不明と書く()
    {
        var store = new NewsCollectionStatusStore();
        store.Record(status: null, validFor: null, Now);
        var llm = new RecordingLlm();

        var decision = await NewService(llm, store, budget: null).DecideAsync(ScheduledAapl());

        decision.Should().NotBeNull();
        llm.Prompts.Should().OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.NewsUnknownLine));
    }

    private static AppSvc NewService(RecordingLlm llm, NewsCollectionStatusStore store, int? budget) =>
        new(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: DecisionOrchestrationOptions.Default with { EnableScreening = true, ScreeningContextBudgetChars = budget },
            currentPrice: new FakeCurrentPrice(new CurrentPriceReading(102m, IntradayPriceContext.Unknown)),
            newsStatus: store);

    private static string Section(string prompt, string heading)
    {
        var start = prompt.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"見出し {heading} が無い");
        var end = prompt.IndexOf("\n#", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? prompt[start..] : prompt[start..end];
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class RecordingLlm : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult("""{"action":"Buy","rationale":"押し目","referencePrice":102,"stopLossDistancePerShare":2}""");
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
