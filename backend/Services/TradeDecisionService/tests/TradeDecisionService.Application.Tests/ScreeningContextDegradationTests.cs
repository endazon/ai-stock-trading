using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Application.Tests;

// FR-02, FR-04, ADR-0003, #337, IADR-0247: スクリーニング入力の縮退の**結線**検証
// （縮退順序そのものの網羅は ScreeningContextPlannerTests が持つ。ここでは
//  「プロンプトに何が残るか」「保護対象が削られないこと」「発生が記録されること」を通しで固定する）。
public class ScreeningContextDegradationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    // 呼び出し順にプロンプトを記録する LLM スタブ（1 回目＝一次スクリーニング・2 回目以降＝二次本判断）。
    private sealed class RecordingLlm(string output) : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(output);
        }
    }

    private sealed class FakePolicy(DailyPolicy policy) : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new SizingContext(
                100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    private sealed class FakeRetrieval(IReadOnlyList<RetrievedContext> hits) : IRetrievalContextProvider
    {
        public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
            DecisionTrigger trigger, DailyPolicy policy, CancellationToken ct = default) => Task.FromResult(hits);
    }

    private sealed class RecordingReporter : IScreeningReductionReporter
    {
        public List<ScreeningContextReduced> Reported { get; } = [];

        public Task ReportAsync(ScreeningContextReduced reduction, CancellationToken ct = default)
        {
            Reported.Add(reduction);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReporter : IScreeningReductionReporter
    {
        public Task ReportAsync(ScreeningContextReduced reduction, CancellationToken ct = default) =>
            throw new InvalidOperationException("記録発行の擬似障害");
    }

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "日本株の押し目買い方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now);

    // 参考情報（出所タグで分類される）。本文 100 文字で概算サイズを揃える。
    private static RetrievedContext Ref(string title, string tag, double score) =>
        new(title, new string('あ', 100), SourceUri: null, score, [tag]);

    private static readonly RetrievedContext MarketData = Ref("当日市況データ", "finnhub", 0.5);   // 保護
    private static readonly RetrievedContext RagNote = Ref("過去の振り返りメモ", "report", 0.3);   // 段 2
    private static readonly RetrievedContext NewsHigh = Ref("重要ニュース", "google-news", 0.9);   // 段 3（残る側）
    private static readonly RetrievedContext NewsLow = Ref("低関連ニュース", "sec-edgar", 0.1);    // 段 3（先に削る側）

    private static (AppSvc Service, RecordingLlm Llm, RecordingReporter Reporter) Create(
        int? budget, IReadOnlyList<RetrievedContext>? hits = null, IScreeningReductionReporter? reporter = null)
    {
        var llm = new RecordingLlm(BuyJson);
        var recording = new RecordingReporter();
        var service = new AppSvc(
            llm, new FakePolicy(Policy), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: new FakeRetrieval(hits ?? [MarketData, RagNote, NewsHigh, NewsLow]),
            options: DecisionOrchestrationOptions.Default with
            {
                EnableScreening = true,
                ScreeningContextBudgetChars = budget,
            },
            screeningReporter: reporter ?? recording);
        return (service, llm, recording);
    }

    [Fact]
    public async Task 予算内ならすべての参考情報がスクリーニングへ載り記録は出ない()
    {
        var (service, llm, reporter) = Create(budget: 5_000);

        await service.DecideAsync(Trigger());

        var screeningPrompt = llm.Prompts[0];
        screeningPrompt.Should().Contain("当日市況データ");
        screeningPrompt.Should().Contain("過去の振り返りメモ");
        screeningPrompt.Should().Contain("重要ニュース");
        screeningPrompt.Should().Contain("低関連ニュース");
        reporter.Reported.Should().BeEmpty("縮退が発生していないのに記録すると件数が水増しされる");
    }

    [Fact]
    public async Task 予算超過なら段2のRAGと段3の低関連ニュースが削られ発生が記録される()
    {
        // 保護分（骨格 600 + 方針 11 + 銘柄行 120 + 市況 168）≒ 899。予算 1080 → 材料は 181 文字分まで。
        // RAG（171）を削っても足りず、段 3 で関連度の低いニュース（171）を削って収まる。
        var (service, llm, reporter) = Create(budget: 1_080);

        await service.DecideAsync(Trigger());

        var screeningPrompt = llm.Prompts[0];
        // 否定形の対: 削られたものは載らない。
        screeningPrompt.Should().NotContain("過去の振り返りメモ", "段 2: RAG は先に削られる");
        screeningPrompt.Should().NotContain("低関連ニュース", "段 3: ニュースは関連度の低い順に削られる");
        // 肯定形: 保護対象と高関連ニュースは残る。
        screeningPrompt.Should().Contain("当日市況データ", "市況・価格データは保護対象（削らない）");
        screeningPrompt.Should().Contain("重要ニュース");
        screeningPrompt.Should().Contain(Policy.Summary, "確定した日報の方針は保護対象（削らない）");

        var reported = reporter.Reported.Should().ContainSingle().Which;
        reported.Split.Should().BeFalse("銘柄 1 件の呼び出しでは分割は起きない（分割と切り詰めは別勘定）");
        reported.Truncated.Should().BeTrue();
        reported.DroppedRagCount.Should().Be(1);
        reported.DroppedNewsCount.Should().Be(1);
        reported.BatchCount.Should().Be(1);
        reported.Symbols.Should().BeEquivalentTo(["AAPL"]);
    }

    [Fact]
    public async Task 全材料を削っても収まらない場合も保護対象は削らず超過を記録する_否定形()
    {
        // 予算 500 は保護分（≒899）未満。材料はすべて削られるが、方針・市況は**削れない**まま呼び出し、
        // 解消不能な超過として記録する（上位モデルへの退避もしない）。
        var (service, llm, reporter) = Create(budget: 500);

        await service.DecideAsync(Trigger());

        var screeningPrompt = llm.Prompts[0];
        screeningPrompt.Should().Contain(Policy.Summary, "全段を使い切っても方針（保護対象）は削られない");
        screeningPrompt.Should().Contain("当日市況データ", "全段を使い切っても市況（保護対象）は削られない");
        screeningPrompt.Should().NotContain("過去の振り返りメモ");
        screeningPrompt.Should().NotContain("重要ニュース");

        var reported = reporter.Reported.Should().ContainSingle().Which;
        reported.UnresolvableOverflow.Should().BeTrue();
        reported.DroppedRagCount.Should().Be(1);
        reported.DroppedNewsCount.Should().Be(2);
    }

    [Fact]
    public async Task 予算未設定ならスクリーニングへ参考情報を載せない_従来挙動()
    {
        // IADR-0072 決定2 の従来挙動: 縮退制御が無効（既定）の一次スクリーニングは方針＋銘柄のみ。
        var (service, llm, reporter) = Create(budget: null);

        await service.DecideAsync(Trigger());

        var screeningPrompt = llm.Prompts[0];
        screeningPrompt.Should().NotContain("参考情報（ナレッジベース）");
        screeningPrompt.Should().NotContain("当日市況データ");
        reporter.Reported.Should().BeEmpty();
        // 対の肯定形: 本判断プロンプト（2 回目）には従来どおり参考情報が載る。
        llm.Prompts[1].Should().Contain("当日市況データ");
    }

    [Fact]
    public async Task 記録発行が例外でも判断は継続する_failsafe()
    {
        var (service, _, _) = Create(budget: 500, reporter: new ThrowingReporter());

        var decision = await service.DecideAsync(Trigger());

        decision.Should().NotBeNull("縮退の記録はクリティカルパス外であり、失敗しても判断を壊さない");
    }

    [Fact]
    public async Task イベントは分割と切り詰めを別のフラグで運ぶ()
    {
        // planning#53 の裁定「分割（材料は減らない）と切り詰め（材料が減る）は分けて数える」を契約面で固定する。
        var reduced = new ScreeningContextReduced(
            ["AAPL"], BatchCount: 3, Split: true, DroppedRagCount: 0, DroppedNewsCount: 0,
            UnresolvableOverflow: false, BudgetChars: 1_000, Now);

        reduced.Split.Should().BeTrue();
        reduced.Truncated.Should().BeFalse("分割だけでは材料は減っていない（切り詰めとして数えない）");

        var truncated = reduced with { Split = false, DroppedNewsCount = 2 };
        truncated.Truncated.Should().BeTrue();
        await Task.CompletedTask;
    }
}
