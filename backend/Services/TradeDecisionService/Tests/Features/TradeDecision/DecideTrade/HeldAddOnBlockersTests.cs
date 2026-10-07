extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-10, FR-04, ADR-0003, #1130, IADR-0471: 保有中の銘柄で、リスク管理の新規建ての可否の口（審査と同じ述語）が保有の方向の新規建て
// （買い増し・売り増し）を必ず拒否すると答えたら、LLM は呼んだうえで（決済の判断を残す）プロンプトで選べないと伝え、LLM が返しても
// 発注せず Hold に倒す（判断後の見送り）。照会の失敗・未結線・空では従来どおり。#1113 の経路（保有 0・未約定なし）は変えない。
public class HeldAddOnBlockersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 15, 41, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 30), "押し目は買い増しを検討する方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"買い増しを検討できる局面","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    // 呼ばれた順に outputs を返す（使い切ったら最後の値を返し続ける）。
    private sealed class RecordingLlm(params string[] outputs) : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(outputs[Math.Min(Prompts.Count, outputs.Length) - 1]);
        }
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
                BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    // 保有照会（実結線）。held が null なら不明。working は null なら不明、true なら未約定あり。
    private sealed class FakeHeld(int? held, bool? working = false) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(held switch
            {
                null => null,
                0 => HeldPosition.None,
                { } q => new HeldPosition(q, 1_000m, q > 0 ? 900m : 1_100m),
            });

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(working switch
            {
                null => null,
                true => new WorkingEntryOrders([new WorkingEntryOrder(TradeSide.Buy, 5, 1_000m, Now.AddMinutes(-1))]),
                false => WorkingEntryOrders.None,
            });
    }

    private sealed class FakeBlockers(EntryBlockers? answer, bool throws = false) : IEntryBlockersProvider
    {
        public EntryBlockers? Answer { get; set; } = answer;

        public List<(string Symbol, Market Market)> Calls { get; } = [];

        public Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls.Add((symbol, market));
            return throws ? throw new HttpRequestException("リスク管理に届かない") : Task.FromResult(Answer);
        }
    }

    private sealed class RecordingForgone : IDecisionForgoneBeforeLlmReporter
    {
        public List<TradeDecisionForgoneBeforeLlm> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default)
        {
            Reports.Add(forgone);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHeldReporter : IDecisionHeldReporter
    {
        public List<TradeDecisionHeld> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default)
        {
            Reports.Add(held);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private sealed record Probe(
        AppSvc Service, RecordingLlm Llm, RecordingForgone Forgone, RecordingHeldReporter Held, RecordingSkips Skips);

    private static Probe Create(
        IHeldPositionProvider held, IEntryBlockersProvider? blockers, string llmOutput, bool screening = false,
        string? mainOutput = null, int? screeningBudget = null)
    {
        var llm = mainOutput is null ? new RecordingLlm(llmOutput) : new RecordingLlm(llmOutput, mainOutput);
        var forgone = new RecordingForgone();
        var heldReporter = new RecordingHeldReporter();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: screening
                ? new DecisionOrchestrationOptions { EnableScreening = true, ScreeningContextBudgetChars = screeningBudget }
                : null,
            heldPosition: held, skipReporter: skips, heldReporter: heldReporter, forgoneReporter: forgone,
            entryBlockers: blockers);
        return new Probe(service, llm, forgone, heldReporter, skips);
    }

    private static DecisionTrigger Trigger() => DecisionTrigger.Scheduled("MSFT", Market.UnitedStates, Now);

    private static EntryBlockers LongBlocked(params RejectionReason[] reasons) => new(reasons, []);

    private static EntryBlockers ShortBlocked(params RejectionReason[] reasons) => new([], reasons);

    private static string MainPrompt(Probe probe) =>
        probe.Llm.Prompts.Should().ContainSingle(p => !p.Contains("一次スクリーニング担当", StringComparison.Ordinal)).Subject;

    private static string ScreeningPrompt(Probe probe) =>
        probe.Llm.Prompts.Should().ContainSingle(p => p.Contains("一次スクリーニング担当", StringComparison.Ordinal)).Subject;

    // T-10-1901: 保有中・保有の方向が塞がり → LLM は呼び（一次も本判断も）、プロンプトは買い増し・売り増しを選べないと理由つきで書く。
    // 照会は 1 回（判断対象の銘柄・市場）。LLM を呼ぶ前の見送りは出さない（#1113 の経路ではない）。
    [Theory]
    [InlineData(10, "買い増し（Buy）", "手仕舞い（Sell）")]
    [InlineData(-10, "売り増し（Sell）", "手仕舞い（Buy）")]
    public async Task T_10_1901_保有中で塞がっているとプロンプトが買い増しを選べないと書きLLMは呼ぶ(
        int held, string addOn, string close)
    {
        var blocked = held > 0
            ? LongBlocked(RejectionReason.MaxPositionsExceeded)
            : ShortBlocked(RejectionReason.MaxPositionsExceeded);
        var blockers = new FakeBlockers(blocked);
        var probe = Create(
            new FakeHeld(held), blockers, SellJson, screening: true,
            mainOutput: """{"action":"Hold","rationale":"様子見"}""");

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Prompts.Should().HaveCount(2, "一次（関心あり）と本判断の両方を呼ぶ");
        blockers.Calls.Should().Equal([("MSFT", Market.UnitedStates)], "保有中でも 1 回だけ照会する");
        probe.Forgone.Reports.Should().BeEmpty("LLM を呼ぶ前の見送りではない");

        var main = MainPrompt(probe);
        main.Should().Contain($"本日は{addOn}を選べません。{TradeDecisionPromptBuilder.AddOnBlockedReasonLead}（理由: 保有建玉数の上限に到達）。");
        main.Should().Contain($"保有継続（Hold）・{close}のいずれかを判断します。{TradeDecisionPromptBuilder.AddOnBlockedConversionNote}");
        main.Should().NotContain(TradeDecisionPromptBuilder.AddOnlyWithinPolicyRule, "選べない行動の条件は出さない");
        main.Should().NotContain(TradeDecisionPromptBuilder.NoAddAtStopLossLineRule);
        main.Should().Contain(TradeDecisionPromptBuilder.ExitFollowsPolicyRule, "出口の行は残す");

        var screeningPrompt = ScreeningPrompt(probe);
        screeningPrompt.Should().Contain($"本日は{addOn}を選べません。");
        screeningPrompt.Should().Contain(TradeDecisionPromptBuilder.ScreeningAddOnBlockedTail);
        screeningPrompt.Should().NotContain(TradeDecisionPromptBuilder.ScreeningHeldRule);
    }

    // T-10-1909: 予算付きの一次（`ScreeningContextBudgetChars` を設定した縮退制御ありの経路）にも、選べない旨が渡る
    // （独立監査の変異 M17: この分岐へ塞がりを渡さなくても T-10-1901 は緑のままだった）。
    [Theory]
    [InlineData(10, "買い増し（Buy）")]
    [InlineData(-10, "売り増し（Sell）")]
    public async Task T_10_1909_予算付きの一次にも買い増しを選べない旨が渡る(int held, string addOn)
    {
        var blocked = held > 0
            ? LongBlocked(RejectionReason.MaxPositionsExceeded)
            : ShortBlocked(RejectionReason.MaxPositionsExceeded);
        var probe = Create(
            new FakeHeld(held), new FakeBlockers(blocked), SellJson, screening: true,
            mainOutput: """{"action":"Hold","rationale":"様子見"}""",
            screeningBudget: DecisionOrchestrationOptions.DefaultScreeningContextBudgetChars);

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        var screeningPrompt = ScreeningPrompt(probe);
        screeningPrompt.Should().Contain($"本日は{addOn}を選べません。");
        screeningPrompt.Should().Contain(TradeDecisionPromptBuilder.ScreeningAddOnBlockedTail);
        screeningPrompt.Should().NotContain(TradeDecisionPromptBuilder.ScreeningHeldRule);
        MainPrompt(probe).Should().Contain($"本日は{addOn}を選べません。");
    }

    // T-10-1902: 🔴 LLM が買い増し（ロングの Buy）・売り増し（ショートの Sell）を返しても発注意図を作らず Hold に倒す。
    // 判断後の見送り（TradeDecisionHeld）を理由つきで 1 件出し（監査台帳）、計上も同じ理由で 1 件。LLM を呼ぶ前の見送りは出さない。
    [Theory]
    [InlineData(10, BuyJson)]
    [InlineData(-10, SellJson)]
    public async Task T_10_1902_LLMの買い増しは発注せずHoldに倒し判断後の見送りを出す(int held, string llmOutput)
    {
        var blocked = held > 0
            ? LongBlocked(RejectionReason.MaxPositionsExceeded, RejectionReason.StoppedOutSameDay)
            : ShortBlocked(RejectionReason.KillSwitchActive);
        var probe = Create(new FakeHeld(held), new FakeBlockers(blocked), llmOutput);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull("審査で必ず落ちる買い増しを発注意図にしない");
        probe.Llm.Prompts.Should().NotBeEmpty("LLM は呼んだ");
        probe.Skips.Reasons.Should().Equal([DecisionSkipReason.AddOnBlockedByRiskControls]);
        var e = probe.Held.Reports.Should().ContainSingle().Subject;
        e.Reason.Should().Be(nameof(DecisionSkipReason.AddOnBlockedByRiskControls));
        e.Symbol.Should().Be("MSFT");
        e.Market.Should().Be(Market.UnitedStates);
        e.Price.Should().Be(1_000m, "判断時点の価格（現在値・起点の価格が無いので LLM の参照価格）");
        e.CycleTrigger.Should().Be(Trigger().MetricTrigger);
        probe.Forgone.Reports.Should().BeEmpty("LLM を呼んでいる（LLM を呼ぶ前の見送りではない）");
    }

    // T-10-1903: 🔴 決済の判断は必ず残す。両方向が塞がっていても、ロング保有の売り・ショート保有の買いは全量の決済として出る。
    [Theory]
    [InlineData(10, SellJson, TradeSide.Sell)]
    [InlineData(-10, BuyJson, TradeSide.Buy)]
    public async Task T_10_1903_塞がっていても決済は全量で通る(int held, string llmOutput, TradeSide side)
    {
        var both = new EntryBlockers(
            [RejectionReason.KillSwitchActive, RejectionReason.MaxPositionsExceeded],
            [RejectionReason.KillSwitchActive, RejectionReason.MaxPositionsExceeded]);
        var probe = Create(new FakeHeld(held), new FakeBlockers(both), llmOutput);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Side.Should().Be(side);
        decision.Intent.Quantity.Should().Be(Math.Abs(held));
        probe.Skips.Reasons.Should().BeEmpty();
        probe.Held.Reports.Should().BeEmpty();
    }

    public static TheoryData<string> UnchangedCases() =>
    [
        "照会の失敗（不明）", "照会の例外", "未結線", "塞がりなし", "反対方向だけ塞がり",
    ];

    private static IEntryBlockersProvider? UnchangedBlockers(string name) => name switch
    {
        "照会の失敗（不明）" => new FakeBlockers(null),
        "照会の例外" => new FakeBlockers(null, throws: true),
        "未結線" => null,
        "塞がりなし" => new FakeBlockers(EntryBlockers.None),
        // ロング保有で売りの新規建て（ShortSide）だけ塞がり＝買い増しは塞がっていない。
        _ => new FakeBlockers(ShortBlocked(RejectionReason.StoppedOutSameDay)),
    };

    // T-10-1904: 照会の失敗・例外・未結線・空・反対方向だけ塞がりでは、プロンプトは未結線の構成と一字一句同じで、
    // LLM の買い増しは従来どおり新規建ての判断として審査へ届く（窓の表の増える側: T0 で空なら審査が止める）。
    [Theory]
    [MemberData(nameof(UnchangedCases))]
    public async Task T_10_1904_不明や空ではプロンプトも結論も従来どおり(string name)
    {
        var baseline = Create(new FakeHeld(10), blockers: null, BuyJson, screening: true);
        await baseline.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        var probe = Create(new FakeHeld(10), UnchangedBlockers(name), BuyJson, screening: true);
        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Prompts.Should().Equal(baseline.Llm.Prompts, $"{name}: プロンプトは従来と同じ");
        MainPrompt(probe).Should().Contain(
            "この銘柄は保有中です。買い増し（Buy）・保有継続（Hold）・手仕舞い（Sell）のいずれかを判断します。", name);
        decision.Should().NotBeNull(name);
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open, name);
        decision.Intent.Side.Should().Be(TradeSide.Buy, name);
        probe.Skips.Reasons.Should().BeEmpty(name);
        probe.Held.Reports.Should().BeEmpty(name);
    }

    // T-10-1905: 変換は判断ごとの答えで決まり、状態を持たない（窓の表の減る側: 次の判断で空なら買い増しは審査へ届く）。
    [Fact]
    public async Task T_10_1905_次の判断で塞がりが解けていれば買い増しは審査へ届く()
    {
        var blockers = new FakeBlockers(LongBlocked(RejectionReason.MaxPositionsExceeded));
        var probe = Create(new FakeHeld(10), blockers, BuyJson);

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();

        blockers.Answer = EntryBlockers.None;
        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        blockers.Calls.Should().HaveCount(2, "判断ごとに読み直す");
        probe.Skips.Reasons.Should().Equal([DecisionSkipReason.AddOnBlockedByRiskControls], "1 回目だけ");
    }

    // T-10-1908: #1113 の経路（保有 0・未約定なし・買いが塞がり）は変わらない（LLM 0 回・LLM を呼ぶ前の見送り・照会 1 回）。
    // 保有 0 で未約定あり／不明・保有不明は照会しない（従来どおり。本件は保有中だけを足した）。
    [Theory]
    [InlineData("保有0", 1)]
    [InlineData("未約定あり", 0)]
    [InlineData("未約定不明", 0)]
    [InlineData("保有不明", 0)]
    public async Task T_10_1908_保有中でない銘柄の照会と見送りは従来どおり(string name, int expectedCalls)
    {
        var blockers = new FakeBlockers(LongBlocked(RejectionReason.MaxPositionsExceeded));
        var held = name switch
        {
            "保有0" => new FakeHeld(0),
            "未約定あり" => new FakeHeld(0, working: true),
            "未約定不明" => new FakeHeld(0, working: null),
            _ => new FakeHeld(null),
        };
        var probe = Create(held, blockers, BuyJson);

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        blockers.Calls.Should().HaveCount(expectedCalls, name);
        probe.Skips.Reasons.Should().NotContain(DecisionSkipReason.AddOnBlockedByRiskControls, name);
        if (name == "保有0")
        {
            probe.Llm.Prompts.Should().BeEmpty();
            probe.Forgone.Reports.Should().ContainSingle()
                .Which.Reason.Should().Be(DecisionForgoneBeforeLlmReason.EntryBlockedByRiskControls);
        }
        else
        {
            probe.Llm.Prompts.Should().NotBeEmpty(name);
            probe.Llm.Prompts.Should().NotContain(p => p.Contains("を選べません", StringComparison.Ordinal), name);
        }
    }

    // T-10-1906: プロンプトの組み立て。引数なし・空の一覧は従来と一字一句同じ（既定を変えない）。8 理由（#1176 で 1 つ足した）は日本語名で、並びのまま書く。
    [Fact]
    public void T_10_1906_組み立ては既定と空で従来どおり理由は日本語名で書く()
    {
        var trigger = Trigger();
        var context = new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
            BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());
        var held = new HeldPosition(10, 1_000m, 900m);

        var plain = TradeDecisionPromptBuilder.Build(trigger, Policy, context, held: held, working: WorkingEntryOrders.None);
        TradeDecisionPromptBuilder.Build(trigger, Policy, context, held: held, working: WorkingEntryOrders.None, addOnBlockers: [])
            .Should().Be(plain);
        var plainScreening = TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, context, held: held, working: WorkingEntryOrders.None);
        TradeDecisionPromptBuilder.BuildScreening(
                trigger, Policy, context, held: held, working: WorkingEntryOrders.None, addOnBlockers: [])
            .Should().Be(plainScreening);

        // 保有 0 では塞がりを渡しても節は変わらない（保有中の節にだけ出す）。
        TradeDecisionPromptBuilder.Build(
                trigger, Policy, context, held: HeldPosition.None, working: WorkingEntryOrders.None,
                addOnBlockers: [RejectionReason.MaxPositionsExceeded])
            .Should().Be(TradeDecisionPromptBuilder.Build(
                trigger, Policy, context, held: HeldPosition.None, working: WorkingEntryOrders.None));

        RejectionReason[] all =
        [
            RejectionReason.KillSwitchActive, RejectionReason.TradingPaused, RejectionReason.StoppedOutSameDay,
            RejectionReason.DecisionExitSameDay,
            RejectionReason.GoodFaithViolationLimitReached, RejectionReason.MaxPositionsExceeded,
            RejectionReason.DailyLossLimitReached, RejectionReason.MaxDrawdownReached,
        ];
        // T-10-2319, #1176, IADR-0495 決定3: 判断由来の決済の後の同日・同方向を足して 8 理由。
        all.Should().BeEquivalentTo(EntryStateBlockers.Determinable, "口が返し得る 8 理由をすべて名付ける");
        var blocked = TradeDecisionPromptBuilder.Build(
            trigger, Policy, context, held: held, working: WorkingEntryOrders.None, addOnBlockers: all);
        blocked.Should().Contain(
            "（理由: 全停止（kill switch）中・取引の一時停止中・本日この方向で損切り済み・本日この方向で判断による手仕舞い（利確など）済み・"
            + "Good Faith Violation の件数が停止基準に到達・保有建玉数の上限に到達・日次損失上限に到達・最大ドローダウンに到達）");
        TradeDecisionPromptBuilder.EntryBlockerLabel(RejectionReason.BannedSymbol).Should().Be("BannedSymbol", "対象外は名前のまま");
    }
}
