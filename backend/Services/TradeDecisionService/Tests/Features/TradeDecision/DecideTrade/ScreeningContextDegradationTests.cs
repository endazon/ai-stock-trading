extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// FR-02, FR-04, ADR-0003, #337, #567, IADR-0247, IADR-0313: スクリーニング入力の縮退の**結線**検証
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

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
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
        int? budget, IReadOnlyList<RetrievedContext>? hits = null, IScreeningReductionReporter? reporter = null,
        DailyPolicy? policy = null)
    {
        var llm = new RecordingLlm(BuyJson);
        var recording = new RecordingReporter();
        var service = new AppSvc(
            llm, new FakePolicy(policy ?? Policy), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
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
        // 保護分（骨格 750 + 方針 11 + 銘柄行 120 + 市況 168）≒ 1049。予算 1230 → 材料は 181 文字分まで。
        // RAG（171）を削っても足りず、段 3 で関連度の低いニュース（171）を削って収まる。
        // IADR-0297: 骨格は空売りガードレール短縮版（142 文字・実測）ぶん 600→750 へ底上げ（予算も同幅シフト）。
        var (service, llm, reporter) = Create(budget: 1_230);

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
        // 予算 500 は保護分（≒1049）未満。材料はすべて削られるが、方針・市況は**削れない**まま呼び出し、
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

    // ================================================================================================
    // FR-02, FR-04, #567, IADR-0313: **既定予算**（150,000 文字）での縮退挙動。
    //
    // 上のテスト群は小さな予算を明示的に与えて結線を確認する。ここでは「既定値が現実的な材料サイズで
    // 機能すること」を、統制系の 3 点セット（境界値テーブル・プロパティベース・否定形）で固定する。
    //
    // 🔴 **境界値テーブルとプロパティベースは ScreeningContextPlanner を直接駆動する。**
    // ScreeningContextAssembler 経由では参考情報 1 件あたり 1,160 文字（タイトル 200 + 本文 400 +
    // 出典 500 + JSON 化 60）の上限が掛かり、Retrieval:TopK=5 の現行構成では 150,000 文字へ到達し得ない
    // （IADR-0313 決定5）。結線面（プロンプトへ何が残るか・記録が出るか）は AppService 経由で固定する。
    // ================================================================================================

    private const int DefaultBudget = DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars;

    // 骨格 750 + 方針要約 2,000（共有保護分）と銘柄行 120（銘柄側保護分）を置いた場合の削減可能材料の許容量。
    private const int SharedProtected = 750 + 2_000;
    private const int SymbolProtected = 120;
    private const int MaterialAllowance = DefaultBudget - SharedProtected - SymbolProtected; // 147,130

    private static readonly DateTimeOffset Older = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);

    // 許容量ちょうどに揃えた 4 件（RAG 2 件・ニュース 2 件）。末尾のニュースへ extra 文字を足して超過させる。
    private static List<ScreeningMaterial> Materials(int extra) =>
    [
        new(0, ScreeningMaterialKind.RagReference, 50_000, PublishedAt: null, Relevance: 0.1),
        new(1, ScreeningMaterialKind.RagReference, 40_000, PublishedAt: null, Relevance: 0.9),
        new(2, ScreeningMaterialKind.NewsDisclosure, 30_000, Older, Relevance: 0.5),
        new(3, ScreeningMaterialKind.NewsDisclosure, MaterialAllowance - 120_000 + extra, Newer, Relevance: 0.5),
    ];

    private static ScreeningContextPlan PlanAtDefaultBudget(
        IReadOnlyList<ScreeningMaterial> materials, int sharedProtected = SharedProtected) =>
        ScreeningContextPlanner.Plan(
            sharedProtected, [new ScreeningSymbolLoad("AAPL", SymbolProtected)], materials, DefaultBudget);

    // 境界値テーブル: 既定予算の許容量ちょうど／1 文字超過／段②を使い切っても足りない量、の 3 点で
    // 段の進み方が切り替わる。**1 文字の差で挙動が変わる点を明示的に踏む。**
    [Theory]
    // extra=0: 許容量ちょうど → 1 件も削らない
    [InlineData(0, 0, 0, false)]
    // extra=1: 1 文字超過 → 段②で関連度の最も低い RAG を 1 件だけ削れば収まる（段③へは進まない）
    [InlineData(1, 1, 0, false)]
    // extra=100,000: RAG を使い切っても収まらない → 段③へ進み、古いニュースを 1 件削る
    [InlineData(100_000, 2, 1, false)]
    public void 既定予算の境界で縮退の段が切り替わる(
        int extra, int expectedRag, int expectedNews, bool expectedOverflow)
    {
        var plan = PlanAtDefaultBudget(Materials(extra));

        plan.DroppedRagCount.Should().Be(expectedRag);
        plan.DroppedNewsCount.Should().Be(expectedNews);
        plan.UnresolvableOverflow.Should().Be(expectedOverflow);
        plan.SplitOccurred.Should().BeFalse("銘柄 1 件の呼び出しでは分割は起きない（IADR-0247 決定6・IADR-0313 決定4）");
    }

    [Fact]
    public void 既定予算では段2を使い切るまで段3へ進まない()
    {
        // extra=1（1 文字超過）。段②で RAG 1 件を削れば収まるため、ニュースは 1 件も削られない。
        var plan = PlanAtDefaultBudget(Materials(1));
        var retained = plan.Batches.Should().ContainSingle().Which.Materials;

        plan.DroppedNewsCount.Should().Be(0, "段③は段②を使い切ってからしか進まない");
        retained.Where(m => m.Kind == ScreeningMaterialKind.NewsDisclosure).Should().HaveCount(2);
        // 段②の中では関連度の低い側から削る。
        retained.Should().NotContain(m => m.Kind == ScreeningMaterialKind.RagReference && m.Relevance == 0.1);
        retained.Should().Contain(m => m.Kind == ScreeningMaterialKind.RagReference && m.Relevance == 0.9);
    }

    [Fact]
    public void 既定予算でも段3は古い順に削る()
    {
        var plan = PlanAtDefaultBudget(Materials(100_000));
        var retained = plan.Batches.Should().ContainSingle().Which.Materials;

        retained.Should().NotContain(m => m.PublishedAt == Older, "段③は古い順に削る");
        retained.Should().Contain(m => m.PublishedAt == Newer);
    }

    // プロパティベース: 材料サイズを疑似乱数で振っても、既定予算のもとで不変条件が崩れない。
    // 種を InlineData で固定するため再現可能である（失敗した種はそのまま回帰ケースになる）。
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(2026)]
    [InlineData(567)]
    public void 既定予算での縮退は材料サイズによらず不変条件を保つ(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var count = random.Next(1, 12);
            var materials = Enumerable.Range(0, count)
                .Select(i => new ScreeningMaterial(
                    i,
                    random.Next(2) == 0 ? ScreeningMaterialKind.RagReference : ScreeningMaterialKind.NewsDisclosure,
                    random.Next(1, 60_000),
                    random.Next(2) == 0 ? null : Older.AddDays(random.Next(10)),
                    random.NextDouble()))
                .ToList();

            var plan = PlanAtDefaultBudget(materials);
            var retained = plan.Batches.Should().ContainSingle().Which.Materials;
            var worstProtected = SharedProtected + SymbolProtected;
            var ragCount = materials.Count(m => m.Kind == ScreeningMaterialKind.RagReference);

            // (1) 残す材料は必ず元の集合の部分集合であり、削った件数と辻褄が合う。
            retained.Should().BeSubsetOf(materials);
            retained.Should().HaveCount(count - plan.DroppedRagCount - plan.DroppedNewsCount);

            // (2) 段③へ進むのは段②を使い切った後だけ（縮退順序の不変条件）。
            if (plan.DroppedNewsCount > 0)
            {
                plan.DroppedRagCount.Should().Be(ragCount, "段③は段②を使い切ってからしか進まない");
            }

            // (3) 収まったか、収まらないなら UnresolvableOverflow が立つ（黙って超過しない）。
            var total = worstProtected + retained.Sum(m => m.Chars);
            if (plan.UnresolvableOverflow)
            {
                total.Should().BeGreaterThan(DefaultBudget);
            }
            else
            {
                total.Should().BeLessThanOrEqualTo(DefaultBudget);
            }

            // (4) 理由なく削らない（両方向）。**削る順序は関連度・発行時刻で決まりサイズ順ではない**ため、
            //     「最後に削った 1 件を戻すと超える」を書くと実装の並べ替えをテストへ複写することになる。
            //     順序に依存しない形で「必要があるときだけ削る」ことを固定する
            //     （最小限性そのものは境界値テーブルの extra=1〔1 件だけ削る〕が踏んでいる）。
            var fitsWithoutDropping = worstProtected + materials.Sum(m => m.Chars) <= DefaultBudget;
            if (plan.TruncationOccurred)
            {
                fitsWithoutDropping.Should().BeFalse("削る必要が無いのに削ってはならない");
            }
            else
            {
                fitsWithoutDropping.Should().BeTrue("削らずに済むのは、全材料を載せても予算内のときだけ");
            }
        }
    }

    // 否定形（陽性対照）その1: **現行構成の現実的な材料量では、既定予算で縮退が 1 件も起きない。**
    // 参考情報は 1 件あたり最大 1,160 文字で見積もられ、Retrieval:TopK の既定は 5 である（IADR-0313 決定5）。
    // このテストは「既定有効化で監査台帳が増えない」という結論そのものを固定する。
    [Fact]
    public async Task 既定予算では現行構成の現実的な材料量で縮退が起きず記録も出ない_否定形()
    {
        // 各項目を見積り上限（タイトル 200 / 本文 400 / 出典 500）まで埋めた 5 件＝ TopK の既定と同数。
        RetrievedContext Max(string title, string tag, double score) => new(
            title + new string('あ', 200), new string('い', 400), "https://example.com/" + new string('u', 500),
            score, [tag], Older);

        var hits = new[]
        {
            Max("当日市況データ", "finnhub", 0.5),
            Max("過去の振り返りメモ", "report", 0.3),
            Max("重要ニュース", "google-news", 0.9),
            Max("開示情報", "sec-edgar", 0.4),
            Max("低関連ニュース", "edinet", 0.1),
        };
        var (service, llm, reporter) = Create(budget: DefaultBudget, hits: hits);

        await service.DecideAsync(Trigger());

        var screeningPrompt = llm.Prompts[0];
        screeningPrompt.Should().Contain("当日市況データ");
        screeningPrompt.Should().Contain("過去の振り返りメモ", "既定予算では段②が発火しない");
        screeningPrompt.Should().Contain("低関連ニュース", "既定予算では段③が発火しない");
        reporter.Reported.Should().BeEmpty(
            "既定有効化そのものでは監査台帳（ScreeningContextReduced）は増えない（IADR-0313 決定5）");
    }

    // 否定形（陽性対照）その2: 既定予算でも、**保護対象だけで超過すれば削らずに超過を記録する。**
    // 唯一の無上限入力である方針要約（共有保護分）を予算より大きくして踏む。
    [Fact]
    public async Task 既定予算でも保護対象だけの超過は削らず記録する()
    {
        var hugePolicy = new DailyPolicy(new DateOnly(2026, 7, 10), new string('方', DefaultBudget + 10_000));
        var (service, llm, reporter) = Create(budget: DefaultBudget, policy: hugePolicy);

        await service.DecideAsync(Trigger());

        var screeningPrompt = llm.Prompts[0];
        screeningPrompt.Should().Contain(hugePolicy.Summary, "確定した日報の方針は削らない（IADR-0247 決定1）");
        screeningPrompt.Should().Contain("当日市況データ", "当日の市況・価格データは削らない");

        var reported = reporter.Reported.Should().ContainSingle().Which;
        reported.UnresolvableOverflow.Should().BeTrue();
        reported.BudgetChars.Should().Be(DefaultBudget);
        reported.Split.Should().BeFalse("呼び出し形は銘柄ごとの独立呼び出しのまま（IADR-0313 決定4）");
    }
}
