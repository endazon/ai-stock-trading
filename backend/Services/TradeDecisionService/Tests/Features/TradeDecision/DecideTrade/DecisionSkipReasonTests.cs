extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// T-10-663, T-10-667, FR-04, FR-10, NFR-07, #891, IADR-0374:
// 🔴 **見送りは「なぜ見送ったか」を区別して観測できる。**
//
// 従来は `DecideAsync` が `null` を返すだけで、呼び出し元が `RecordTradeDecision(trigger, side: null)` を
// 呼ぶため、**方針なし・Hold・鮮度切れ・数量 0・裸の新規売り・保有不明のすべてが `action=no-trade` の
// 同じ 1 値**に落ちていた。事実は構造化 WARN ログにしか残らず、**保有照会が壊れて新規建てだけが
// 静かに止まっている**状態が「今日は Hold が多い」に埋もれた（#891 の起点）。
//
// 🔴 **本スイートが守るのは「理由が付くこと」だけで、見送るかどうかの判定は 1 ミリも変えていない。**
// どのケースも従来どおり `null`（見送り）であることを併せて表明する。
public class DecisionSkipReasonTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "日本株の押し目買い方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string HoldJson = """{"action":"Hold","rationale":"様子見"}""";
    private const string NonBaseBuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":300000,"stopLossDistancePerShare":7500}""";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FakePolicy(DailyPolicy? policy) : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(policy);
    }

    private sealed class FakeSizing(SizingContext ctx) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(ctx);
    }

    // #292, IADR-0119 / #865, IADR-0358: 保有の供給。null は「不明」（照会不能）であり 0（保有なし）とは別物。
    // 既定は **実結線**（IsEnabled=true）——本物の HttpHeldPositionProvider と同じ側に寄せる。
    private sealed class FakeHeld(int? signedQuantity) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(signedQuantity);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(signedQuantity switch
            {
                null => null,
                0 => HeldPosition.None,
                { } q => new HeldPosition(q, 1_000m, q > 0 ? 970m : 1_030m),
            });
    }

    private sealed class FakeCurrentPrice(decimal? price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<decimal?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(price);
    }

    // #506, IADR-0197: 鮮度切れでも値は返す（「レートが無い」と「古いレートがある」を区別する）。
    private sealed class FakeFxRate(decimal? rate, FxRateFreshness freshness = FxRateFreshness.Fresh)
        : IFxRateProvider
    {
        public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default) =>
            Task.FromResult(freshness == FxRateFreshness.Expired ? null : rate);

        public Task<FxRateReading?> GetReadingAsync(Market market, CancellationToken ct = default) =>
            Task.FromResult(
                rate is not { } r || r <= 0m
                    ? null
                    : new FxRateReading(
                        new FxRate(MarketCurrency.Of(market), MarketCurrency.Base, r,
                            freshness == FxRateFreshness.Expired
                                ? new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
                                : new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero)),
                        freshness));
    }

    /// <summary>報告された見送り理由を記録するだけの偽物（**本スイートの観測点**）。</summary>
    private sealed class RecordingSkipReporter : IDecisionSkipReporter
    {
        public List<(string Trigger, DecisionSkipReason Reason)> Reports { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reports.Add((trigger, reason));
    }

    private static SizingContext Context(decimal stageRemaining = 50_000m, decimal dailyRemaining = 20_000m) =>
        new(100_000m, stageRemaining, dailyRemaining, 0, 0m,
            BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static AppSvc Create(
        RecordingSkipReporter reporter,
        string llmOutput = BuyJson,
        // 🔴 「方針が無い」は `policy: null` では表せない（既定の Policy と区別できない）。専用のフラグで表す。
        bool withoutPolicy = false,
        SizingContext? ctx = null,
        ICurrentPriceProvider? currentPrice = null,
        IFxRateProvider? fxRate = null,
        IHeldPositionProvider? held = null) =>
        new(new FakeLlm(llmOutput), new FakePolicy(withoutPolicy ? null : Policy), new FakeSizing(ctx ?? Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: null, profitabilityOptions: null,
            unconfirmedNotifier: null, currentPrice: currentPrice, fxRate: fxRate, heldPosition: held,
            retrievalSourcePolicy: null, statusNotifier: null, screeningReporter: null,
            skipReporter: reporter);

    // 基準通貨（米国株・USD 建て）の起点。通貨換算の影響を分離する。
    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now);

    // 非基準通貨（日本株・JPY 建て）の起点。鮮度切れの経路を踏むために使う。
    private static PriceMovementDetected NonBaseTrigger() =>
        new(Guid.NewGuid(), "7203", Market.Japan, 315_000m, 300_000m, 0.05m, Now);

    private static async Task<DecisionSkipReason> SkipReasonOf(
        AppSvc service, RecordingSkipReporter reporter, PriceMovementDetected trigger)
    {
        var decision = await service.DecideAsync(trigger, TestContext.Current.CancellationToken);

        decision.Should().BeNull("本スイートは見送りの理由だけを見る（見送るかどうかの判定は変えていない）");
        reporter.Reports.Should().ContainSingle("1 回の判断につき見送りの計上は 1 件だけである");
        return reporter.Reports[0].Reason;
    }

    // 🔴 T-10-663, #891 受け入れ基準 1（本 issue の対象）:
    // **実結線の照会で保有が不明なのに新規建て**——平常時の期待値が 0 件であり、
    // 出ていること自体が「新規建てだけが静かに止まっている」印である。
    [Fact]
    public async Task 保有が不明な新規建ての見送りは専用の理由で数えられる()
    {
        var reporter = new RecordingSkipReporter();
        var service = Create(reporter, BuyJson, held: new FakeHeld(null));

        (await SkipReasonOf(service, reporter, Trigger()))
            .Should().Be(DecisionSkipReason.HoldingsUnknownOpen);
        reporter.Reports[0].Trigger.Should().Be(BusinessMetrics.TriggerPriceMovement);
    }

    // 🔴 T-10-667, #891 やること 1: **見送りの理由は一度に洗い出した語彙で区別される。**
    // 1 件だけ特別扱いすると読み方が割れるため、到達可能な見送り地点を 1 本の表で固定する。
    [Fact]
    public async Task 見送りの理由は地点ごとに異なる値で報告される()
    {
        var observed = new List<DecisionSkipReason>();

        // 1. 確定済み日報の方針が無い（FR-07）
        var r1 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(Create(r1, BuyJson, withoutPolicy: true), r1, Trigger()));

        // 2. 現在値ソースが有効なのに現在値が取れない（IADR-0099 決定3）
        var r2 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(
            Create(r2, BuyJson, currentPrice: new FakeCurrentPrice(null)), r2, Trigger()));

        // 3. 換算レートがまったく解決できない（IADR-0107 決定3）
        var r3 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(
            Create(r3, NonBaseBuyJson, fxRate: new FakeFxRate(null)), r3, NonBaseTrigger()));

        // 4. 換算レートが鮮度切れで保有も無い（LLM を呼ぶ前に倒す・#506）
        var r4 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(
            Create(r4, NonBaseBuyJson, fxRate: new FakeFxRate(0.01m, FxRateFreshness.Expired),
                held: new FakeHeld(0)),
            r4, NonBaseTrigger()));

        // 5. LLM が Hold を返した（平常時に最も多い見送り）
        var r5 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(Create(r5, HoldJson), r5, Trigger()));

        // 6. 実結線の照会で保有が不明なのに新規建て（#865 / IADR-0358。本 issue の対象）
        var r6 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(Create(r6, BuyJson, held: new FakeHeld(null)), r6, Trigger()));

        // 7. 保有なしでの売り＝裸の新規ショート建て（IADR-0119 決定2）
        var r7 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(Create(r7, SellJson, held: new FakeHeld(0)), r7, Trigger()));

        // 8. 換算レートが鮮度切れで、保有はあるが LLM が買い増し（＝新規建て）と言った（ADR-0022 決定5）
        var r8 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(
            Create(r8, NonBaseBuyJson, fxRate: new FakeFxRate(0.01m, FxRateFreshness.Expired),
                held: new FakeHeld(10)),
            r8, NonBaseTrigger()));

        // 9. サイジングで数量 0（残枠が無い）
        var r9 = new RecordingSkipReporter();
        observed.Add(await SkipReasonOf(
            Create(r9, BuyJson, ctx: Context(stageRemaining: 0m, dailyRemaining: 0m), held: new FakeHeld(0)),
            r9, Trigger()));

        observed.Should().Equal(
            DecisionSkipReason.DailyPolicyUnconfirmed,
            DecisionSkipReason.CurrentPriceUnavailable,
            DecisionSkipReason.FxRateUnresolved,
            DecisionSkipReason.FxRateStaleNoHolding,
            DecisionSkipReason.LlmHold,
            DecisionSkipReason.HoldingsUnknownOpen,
            DecisionSkipReason.NakedShortOpen,
            DecisionSkipReason.FxRateStaleOpen,
            DecisionSkipReason.SizingZeroQuantity);
        observed.Should().OnlyHaveUniqueItems("理由が重なると内訳が読めなくなる");
    }

    // 🔴 #891 やること 1（語彙の網羅）: **語彙は 12 値で、洗い出しの結果そのものである。**
    // 値を足したのに報告点を足さない／報告点を消したのに値を残す、を気付けるようにする。
    // 上のテストが 9 値を**振る舞いで**固定し、残る 3 値は到達に LLM 出力の不正（参照価格 0・損切り幅の異常）か
    // 採算ゲートの構成が要るため、ここでは語彙の側だけを固定する（IADR-0374 §結果 に明記）。
    [Fact]
    public void 見送り理由の語彙は洗い出した12値である()
    {
        Enum.GetValues<DecisionSkipReason>().Should().HaveCount(12);
        Enum.GetValues<DecisionSkipReason>().Should().Contain(
        [
            DecisionSkipReason.ReferencePriceInvalid,
            DecisionSkipReason.StopLossDistanceInvalid,
            DecisionSkipReason.ProfitabilityNotViable,
        ]);
    }

    /// <summary>計上のたびに例外を投げる偽物（将来 I/O を持つ実装が挿さった場合の代役）。</summary>
    private sealed class ThrowingSkipReporter : IDecisionSkipReporter
    {
        public int Calls { get; private set; }

        public void Report(string trigger, DecisionSkipReason reason)
        {
            Calls++;
            throw new InvalidOperationException("計上先が壊れている");
        }
    }

    // 🔴 T-10-672, PR #919 監査, IADR-0374: **計上の失敗で見送りを壊さない**（兄弟ポートの ...SafeAsync と同じ規律）。
    // 例外が DecideAsync から漏れると、呼び出し元は decisions{action=no-trade} を計上せず、
    // 見送りが「判断の失敗」へ化ける。見送りは従来どおり null で返り、計上は試みられたことを併せて表明する
    // （try/catch ごと計上を消す改変を「呼ばれていない」で捕まえる）。
    [Fact]
    public async Task 見送り理由の計上が例外を投げても見送りはそのまま返る()
    {
        var reporter = new ThrowingSkipReporter();
        var service = new AppSvc(
            new FakeLlm(HoldJson), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            skipReporter: reporter);

        var act = async () => await service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull("計上の失敗は見送りの判定を変えない");
        reporter.Calls.Should().Be(1);
    }

    // 🔴 否定形: **判断が成立したときは 1 件も計上しない。**
    // 見送りカウンタが「判断の回数」になってしまうと、アラートが常時鳴って意味を失う。
    [Fact]
    public async Task 判断が成立したときは見送りを計上しない()
    {
        var reporter = new RecordingSkipReporter();
        var service = Create(reporter, BuyJson, held: new FakeHeld(0));

        var decision = await service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        reporter.Reports.Should().BeEmpty();
    }
}
