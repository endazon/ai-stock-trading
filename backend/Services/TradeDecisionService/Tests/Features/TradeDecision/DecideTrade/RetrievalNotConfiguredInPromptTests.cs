extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-04, FR-08, ADR-0006, #1525, IADR-0608 決定4: 検索（RAG）を使わない環境（単一ノードのクラウド）では、判断のプロンプトに
// 「この環境では検索を使っていない」を 1 行書く。ニュースの「取得済み」の行は「個々の記事は下の参考情報にあるものだけを渡します」と
// 書くため、参考情報が恒久的に無いことを読めない（仕様書 §RAG を外したときの実測）。
// 🔴 検索を構成した環境（経路 B）のプロンプトは 1 文字も変えない。判断の中身（model・purpose）は変えない。
public class RetrievalNotConfiguredInPromptTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static readonly string Line = $"- {TradeDecisionPromptBuilder.RetrievalNotConfiguredLine}{Environment.NewLine}";
    private static readonly string NewsLine = TradeDecisionPromptBuilder.NewsStatusLine(NewsCollectionStatus.Fetched);

    [Fact]
    public void 検索を使わないとき本判断と一次のニュースの行の直後に1行を書く()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var decision = TradeDecisionPromptBuilder.Build(
            trigger, Policy, Context, news: NewsCollectionStatus.Fetched, retrievalNotConfigured: true);
        var screening = TradeDecisionPromptBuilder.BuildScreening(
            trigger, Policy, Context, news: NewsCollectionStatus.Fetched, retrievalNotConfigured: true);

        decision.Should().Contain(NewsLine + Line);
        screening.Should().Contain(NewsLine + Line);
        TradeDecisionPromptBuilder.RetrievalNotConfiguredLine.Should().Contain("検索").And.Contain("使っていません")
            .And.Contain("「材料となるニュースが無い」とは扱いません");
    }

    [Fact]
    public void 検索を構成した環境のプロンプトは変わらない()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var decision = TradeDecisionPromptBuilder.Build(trigger, Policy, Context, news: NewsCollectionStatus.Fetched);
        var screening = TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context, news: NewsCollectionStatus.Fetched);

        decision.Should().NotContain(TradeDecisionPromptBuilder.RetrievalNotConfiguredLine);
        screening.Should().NotContain(TradeDecisionPromptBuilder.RetrievalNotConfiguredLine);
        // 足すのは 1 行だけで、ほかの文言は同じ（行を除いた全文が一致する）。
        TradeDecisionPromptBuilder.Build(trigger, Policy, Context, news: NewsCollectionStatus.Fetched, retrievalNotConfigured: true)
            .Replace(Line, string.Empty, StringComparison.Ordinal).Should().Be(decision);
        TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context, news: NewsCollectionStatus.Fetched, retrievalNotConfigured: true)
            .Replace(Line, string.Empty, StringComparison.Ordinal).Should().Be(screening);
    }

    // ---- 本番の経路（TradeDecisionAppService）-------------------------------------------------------

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private sealed class RecordingLlm : ILlmCompletionClient
    {
        public List<(string Prompt, string? Model, string? Purpose)> Calls { get; } = [];

        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls.Add((prompt, model, purpose));
            return Task.FromResult(BuyJson);
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

    private sealed class FakeClock : TradeDecisionService.Common.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
    }

    // 検索を構成した取得口（成功・0 件）。
    private sealed class ConfiguredRetrieval : IRetrievalContextProvider
    {
        public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
            DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedContext>>([]);
    }

    // 取得に失敗した取得口（状態 Failed）。未構成ではないので行は足さない。
    private sealed class FailingRetrieval : IRetrievalContextProvider
    {
        public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
            DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RetrievedContext>>([]);

        public Task<RetrievalResult> GetContextWithStatusAsync(
            DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RetrievalResult([], RetrievalStatus.Failed, "http-503", FailedSearches: 1));
    }

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, DateTimeOffset.UtcNow);

    private static DecisionOrchestrationOptions Options => DecisionOrchestrationOptions.Default with
    {
        EnableScreening = true,
        PrimaryModel = "claude-haiku-5-5",
        SecondaryModel = "claude-sonnet-5-5",
    };

    [Fact]
    public async Task 検索が未構成の本番の経路では一次と本判断の両方に1行が載りmodelとpurposeは変わらない()
    {
        var llm = new RecordingLlm();
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: Options);

        var decision = await service.DecideAsync(Trigger());

        decision.Should().NotBeNull("判断は失敗しない（検索なしでも続ける）");
        llm.Calls.Should().HaveCount(2);
        llm.Calls.Should().OnlyContain(c => c.Prompt.Contains(TradeDecisionPromptBuilder.RetrievalNotConfiguredLine));
        llm.Calls.Select(c => (c.Model, c.Purpose)).Should().Equal(
            ("claude-haiku-5-5", LlmPurposes.TradeDecisionScreening), ("claude-sonnet-5-5", LlmPurposes.TradeDecision));
    }

    // #1525, IADR-0608 決定4（2026-11-06 の AI レビューの指摘で是正）: 判定は取得口の状態の値（RetrievalStatus.NotConfigured）で行う。
    // 取得の失敗（Failed）は「検索を使っていない」ではないので、行は載らない。
    [Fact]
    public async Task 検索の取得に失敗した本番の経路では1行が載らない()
    {
        var llm = new RecordingLlm();
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            new FailingRetrieval(), options: Options);

        await service.DecideAsync(Trigger());

        llm.Calls.Should().HaveCount(2);
        llm.Calls.Should().OnlyContain(c => !c.Prompt.Contains(TradeDecisionPromptBuilder.RetrievalNotConfiguredLine));
    }

    [Fact]
    public async Task 検索を構成した本番の経路では1行が載らない()
    {
        var llm = new RecordingLlm();
        var service = new AppSvc(llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            new ConfiguredRetrieval(), options: Options);

        await service.DecideAsync(Trigger());

        llm.Calls.Should().HaveCount(2);
        llm.Calls.Should().OnlyContain(c => !c.Prompt.Contains(TradeDecisionPromptBuilder.RetrievalNotConfiguredLine));
    }
}
