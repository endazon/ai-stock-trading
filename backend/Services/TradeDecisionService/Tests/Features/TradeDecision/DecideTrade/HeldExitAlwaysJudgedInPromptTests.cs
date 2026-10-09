extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-04, FR-10, ADR-0003, #1292, IADR-0523（オーナー裁定 2026-10-10）: 取引判断のプロンプトの保有状況節（本判断・一次スクリーニングの両方）に
// 「確定済み方針の銘柄の列挙は新規建ての対象を定める。保有中の銘柄の手仕舞い（利確・損切り）は、列挙に関係なく常に判断する」を固定文で置く。
// 実測（PoC 2026-10-09 US）: 一次の LLM が「確定済み方針の監視銘柄 8 銘柄に含まれていない…取引対象外」として保有中の AMZN・GOOGL を
// 3 サイクルとも Hold にした。#1286（IADR-0521。監視銘柄の外の保有は出口専用で判断に回す）と合わせて、保有の出口が方針の列挙で放棄されないことを固定する。
// T-10-2508〜T-10-2511。
public class HeldExitAlwaysJudgedInPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.Zero);

    // PoC の形: 方針の本文が監視銘柄 8 件を列挙し、保有中の GOOGL を含まない。
    private static readonly DailyPolicy Policy = new(
        new DateOnly(2026, 10, 10),
        "監視銘柄: NVDA, META, TSLA, COIN, MARA, MSTR, SMCI, PLTR の 8 銘柄。押し目で買う。利確: 全銘柄 +5%");

    private static readonly IReadOnlyList<WatchedSymbol> Watchlist =
        new[] { "NVDA", "META", "TSLA", "COIN", "MARA", "MSTR", "SMCI", "PLTR" }
            .Select(s => new WatchedSymbol(s, Market.UnitedStates)).ToList();

    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private const string HeldHeading = TradeDecisionPromptBuilder.HeldPositionSectionTitle;

    private static string RuleLine => $"- {TradeDecisionPromptBuilder.HeldExitAlwaysJudgedRule}";

    private static SizingContext Context(StopLossExecutionMethod? method = StopLossExecutionMethod.SoftwareStop) =>
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits())
        {
            StopLossMethod = method,
        };

    private static DecisionTrigger Trigger(string symbol, bool exitOnly) =>
        DecisionTrigger.Scheduled(symbol, Market.UnitedStates, Now, exitOnly);

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    // 保有状況節（見出しから次の空行まで）の行。
    private static string[] HeldSection(string prompt)
    {
        var lines = Lines(prompt);
        var start = Array.IndexOf(lines, HeldHeading);
        start.Should().BeGreaterThanOrEqualTo(0, "保有状況節は無条件で出る");
        return lines.Skip(start).TakeWhile(l => l.Length > 0).ToArray();
    }

    public static TheoryData<int, bool, StopLossExecutionMethod?> HeldCases => new()
    {
        { 10, false, StopLossExecutionMethod.SoftwareStop },
        { -10, false, StopLossExecutionMethod.SoftwareStop },
        { 10, true, StopLossExecutionMethod.NoProtectiveStop },
        { -10, true, null },
    };

    // T-10-2508: 保有ありなら、本判断・一次の両方の保有状況節に固定文が 1 回だけ出る。買い増しが塞がっていても、損切りの実行機構に依らず出る。
    // 位置は本判断が「保護の状態」の直後、一次が「保有:」の行の直後（既存の行の並び〔出口の規則の直後に利確の行〕を崩さない）。
    [Theory]
    [MemberData(nameof(HeldCases))]
    public void T_10_2508_保有ありなら本判断と一次の両方の保有状況節に手仕舞いを常に判断する固定文が出る(
        int qty, bool addOnBlocked, StopLossExecutionMethod? method)
    {
        var held = new HeldPosition(qty, 100m, qty > 0 ? 90m : 110m);
        IReadOnlyList<RejectionReason>? blockers = addOnBlocked ? [RejectionReason.MaxPositionsExceeded] : null;
        var trigger = Trigger("GOOGL", exitOnly: false);

        var main = HeldSection(TradeDecisionPromptBuilder.Build(
            trigger, Policy, Context(method), currentPrice: 104m, held: held, working: WorkingEntryOrders.None,
            watchlist: Watchlist, addOnBlockers: blockers));
        var screening = HeldSection(TradeDecisionPromptBuilder.BuildScreening(
            trigger, Policy, Context(method), currentPrice: 104m, held: held, working: WorkingEntryOrders.None,
            watchlist: Watchlist, addOnBlockers: blockers));

        main.Count(l => l == RuleLine).Should().Be(1);
        screening.Count(l => l == RuleLine).Should().Be(1);
        Array.IndexOf(main, RuleLine).Should().Be(
            Array.FindIndex(main, l => l.StartsWith("- 保護の状態: ", StringComparison.Ordinal)) + 1);
        Array.IndexOf(screening, RuleLine).Should().Be(
            Array.FindIndex(screening, l => l.StartsWith("- 保有: ", StringComparison.Ordinal)) + 1);

        // 裁定の 2 点（列挙は新規建ての対象・手仕舞いは列挙に関係なく常に判断）を文言として固定する。
        TradeDecisionPromptBuilder.HeldExitAlwaysJudgedRule.Should()
            .Contain("確定済み方針の銘柄の列挙は、新規建ての対象を定めます。")
            .And.Contain("保有中の銘柄の手仕舞い（利確・損切り）は、列挙に関係なく常に判断します");
    }

    // T-10-2508（否定形）: 保有なし・不明・未約定だけでは手仕舞いが成立しないため、固定文を出さない（保有なしの新規建ての判断を変えない）。
    [Fact]
    public void T_10_2508_保有なしと不明と未約定だけでは固定文を出さない_否定形()
    {
        var trigger = Trigger("NVDA", exitOnly: false);
        var working = new WorkingEntryOrders([new WorkingEntryOrder(TradeSide.Buy, 5, 100m, Now)]);
        var cases = new (HeldPosition? Held, WorkingEntryOrders? Working)[]
        {
            (HeldPosition.None, WorkingEntryOrders.None),
            (null, WorkingEntryOrders.None),
            (HeldPosition.None, null),
            (HeldPosition.None, working),
        };

        foreach (var (held, w) in cases)
        {
            TradeDecisionPromptBuilder.Build(trigger, Policy, Context(), currentPrice: 104m, held: held, working: w, watchlist: Watchlist)
                .Should().NotContain(TradeDecisionPromptBuilder.HeldExitAlwaysJudgedRule);
            TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context(), currentPrice: 104m, held: held, working: w, watchlist: Watchlist)
                .Should().NotContain(TradeDecisionPromptBuilder.HeldExitAlwaysJudgedRule);
        }
    }

    // 🔴 T-10-2509: PoC の場面 —— 方針の本文が 8 銘柄を列挙し、保有中の GOOGL は列挙にも監視銘柄にも無い。定時サイクルが GOOGL を
    // 出口専用（#1286）で判断に回すと、一次・本判断の両方のプロンプトに、保有状況節（保有あり）と固定文と出口専用の行が出て、
    // LLM が手仕舞いを返せば保有全量の決済になる（方針の列挙を理由に出口を放棄しない）。
    [Fact]
    public async Task T_10_2509_方針の列挙の外の保有銘柄も一次と本判断の両方で保有状況と固定文を受け取り手仕舞いが決済になる()
    {
        var llm = new RecordingLlm(SellJson);
        var options = new DecisionOrchestrationOptions { EnableScreening = true, ScreeningContextBudgetChars = 150_000 };
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: options, heldPosition: new FakeHeld(10), watchlist: new FakeWatchlist());

        var decision = await service.DecideAsync(Trigger("GOOGL", exitOnly: true), TestContext.Current.CancellationToken);

        llm.Calls.Should().HaveCount(2, "一次で落とさず本判断へ進む");
        llm.Calls[0].Purpose.Should().Be(LlmPurposes.TradeDecisionScreening);
        Policy.Summary.Should().NotContain("GOOGL", "前提: 方針の銘柄の列挙に GOOGL は無い");
        foreach (var (prompt, _) in llm.Calls)
        {
            prompt.Should().Contain($"判断対象の GOOGL（市場: UnitedStates）{TradeDecisionPromptBuilder.WatchlistNotContainsSuffix}");
            prompt.Should().Contain(TradeDecisionPromptBuilder.ExitOnlyLine);
            var section = HeldSection(prompt);
            section.Should().Contain(l => l.StartsWith("- 保有: ロング 10 株", StringComparison.Ordinal));
            section.Should().Contain(RuleLine);
        }

        decision.Should().NotBeNull("保有の出口は方針の列挙で放棄しない");
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Side.Should().Be(TradeSide.Sell);
        decision.Intent.Quantity.Should().Be(10);
    }

    // T-10-2510: 固定文の行（行頭の「- 」と改行を含む）は一次の縮退の保護分（HeldExitRuleReserveChars）に収まる。
    // 予約が銘柄ごとの保護分に入っていることは T-10-2511 が予算の境界で固定する（既存の境界試験は余裕が予約を超えており固定しない）。
    [Fact]
    public void T_10_2510_固定文の行は一次の縮退の保護分に収まる()
    {
        (RuleLine.Length + Environment.NewLine.Length).Should().BeLessThanOrEqualTo(ScreeningContextAssembler.HeldExitRuleReserveChars);
    }

    // 🔴 T-10-2511（#1293 の監査 F1）: 予約は銘柄ごとの保護分（PerSymbolLineChars）に入っている。保護分（予約を含む）と材料 1 件で
    // ちょうどの予算では材料を削らず、1 文字少ない予算では 1 件削る。予約を保護分から外すと、1 文字少ない予算でも削られず赤になる。
    // 方針は「利確:」行を持たない（利確の予約 TakeProfitReachedReserveChars を掛けない）。監視銘柄は不明の形。
    [Fact]
    public void T_10_2511_固定文の行の予約は銘柄ごとの保護分に入り予算の境界で材料の削減が切り替わる_境界値()
    {
        var policy = new DailyPolicy(new DateOnly(2026, 10, 10), "押し目で買う");
        var trigger = Trigger("GOOGL", exitOnly: true);
        var news = new RetrievedContext("記事", new string('あ', 100), SourceUri: null, 0.5, ["google-news"], Now);
        var exactBudget = 750 + policy.Summary.Length + TradeDecisionPromptBuilder.WatchlistSection(trigger, null).Length
            + 400 + ScreeningContextAssembler.PriceContextReserveChars + ScreeningContextAssembler.NewsStatusReserveChars
            + ScreeningContextAssembler.HeldExitRuleReserveChars
            + ("記事".Length + 100 + 60);

        ScreeningContextAssembler.Assemble(trigger, policy, [news], currentPrice: null, budgetChars: exactBudget, watchlist: null)
            .Plan.DroppedNewsCount.Should().Be(0, "保護分（予約を含む）と材料 1 件で予算ちょうど");
        ScreeningContextAssembler.Assemble(trigger, policy, [news], currentPrice: null, budgetChars: exactBudget - 1, watchlist: null)
            .Plan.DroppedNewsCount.Should().Be(1, "予約が保護分に入っていれば 1 文字の不足で材料が削られる");
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class RecordingLlm(string output) : ILlmCompletionClient
    {
        public List<(string Prompt, string? Purpose)> Calls { get; } = [];

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls.Add((prompt, purpose));
            return Task.FromResult(output);
        }
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context());
    }

    private sealed class FakeWatchlist : IWatchlistProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>?> GetWatchlistAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(Watchlist);

        public Task<IReadOnlyList<WatchedSymbol>?> GetAuthoritativeWatchlistAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(Watchlist);
    }

    private sealed class FakeHeld(int held) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(new HeldPosition(held, 1_000m, held > 0 ? 900m : 1_100m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }
}
