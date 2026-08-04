using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Application.Tests;

// FR-04, FR-07, FR-10, ADR-0003, IADR-0003/0004/0017: 取引判断の中核ロジックの検証。
public class TradeDecisionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class FakeLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken ct = default) =>
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

    // #292, IADR-0119: 保有建玉の供給。null は「不明」（照会不能）で 0（保有なし）とは区別する。
    private sealed class FakeHeld(int? signedQuantity) : IHeldPositionProvider
    {
        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(signedQuantity);
    }

    private sealed class ThrowingHeld : IHeldPositionProvider
    {
        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            throw new InvalidOperationException("建玉照会の擬似障害");
    }

    // 本判断プロンプトを捕捉して RAG 文脈の注入を検証するための LLM スタブ。
    private sealed class CapturingLlm(string output) : ILlmCompletionClient
    {
        public string? LastPrompt { get; private set; }

        public Task<string> CompleteAsync(string prompt, string? model = null, CancellationToken ct = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(output);
        }
    }

    private sealed class FakeRetrieval(IReadOnlyList<RetrievedContext> hits) : IRetrievalContextProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
            DecisionTrigger trigger, DailyPolicy policy, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(hits);
        }
    }

    private sealed class ThrowingRetrieval : IRetrievalContextProvider
    {
        public Task<IReadOnlyList<RetrievedContext>> GetContextAsync(
            DecisionTrigger trigger, DailyPolicy policy, CancellationToken ct = default) =>
            throw new InvalidOperationException("KB 取得の擬似障害");
    }

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "日本株の押し目買い方針");

    private static SizingContext Context(decimal stageRemaining = 50_000m, decimal dailyRemaining = 20_000m,
        int losses = 0, decimal dd = 0m) =>
        new(100_000m, stageRemaining, dailyRemaining, losses, dd, TradeMode.Paper, TradingDefaults.CreateRiskLimits());

    private static AppSvc Create(string llmOutput, DailyPolicy? policy, SizingContext? ctx = null) =>
        new(new FakeLlm(llmOutput), new FakePolicy(policy), new FakeSizing(ctx ?? Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance);

    // #292, IADR-0119: 保有建玉を与える版。既定（Create）は NoOp＝常に不明。
    private static AppSvc CreateWithHeld(
        string llmOutput, IHeldPositionProvider held, SizingContext? ctx = null) =>
        new(new FakeLlm(llmOutput), new FakePolicy(Policy), new FakeSizing(ctx ?? Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance, heldPosition: held);

    // #257, IADR-0107: 本スイートは通貨換算の影響を分離するため基準通貨（日本株・円建て）の銘柄を用いる
    // （価格 1,000 円・損切り幅 30 円として読む）。非基準通貨（米国株）の換算・レート未解決時の見送りは
    // 後段の「通貨換算」節で明示的に検証する。
    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "7203", Market.Japan, 1_040m, 1_000m, 0.04m, Now);

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";

    [Fact]
    public async Task 確定済み日報が無ければ取引しない()
    {
        // FR-07: 確定前方針は不適用。
        var service = Create(BuyJson, policy: null);

        (await service.DecideAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task LLMがHoldなら取引しない()
    {
        var service = Create("""{"action":"Hold","rationale":"様子見"}""", Policy);

        (await service.DecideAsync(Trigger())).Should().BeNull();
    }

    // --- UC-01, FR-09, IADR-0096, #210: 日報未確定による見送り時の通知フックの検証 ---

    private sealed class FakeUnconfirmedNotifier : IDailyPolicyUnconfirmedNotifier
    {
        public int Calls { get; private set; }
        public Task NotifyAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingUnconfirmedNotifier : IDailyPolicyUnconfirmedNotifier
    {
        public Task NotifyAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("通知発行の擬似障害");
    }

    private static AppSvc CreateWithNotifier(
        string llmOutput, DailyPolicy? policy, IDailyPolicyUnconfirmedNotifier notifier) =>
        new(new FakeLlm(llmOutput), new FakePolicy(policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: null, profitabilityOptions: null,
            unconfirmedNotifier: notifier);

    // 基準1: 日報未確定（policy-null）の見送り時に通知フックが呼ばれる。
    [Fact]
    public async Task 日報未確定で見送るとき通知フックが呼ばれる()
    {
        var notifier = new FakeUnconfirmedNotifier();

        (await CreateWithNotifier(BuyJson, policy: null, notifier).DecideAsync(Trigger())).Should().BeNull();

        notifier.Calls.Should().Be(1);
    }

    // 日報が有る（見送り理由が policy-null 以外）ときは日報未確定通知を出さない（他の見送りに波及しない）。
    [Fact]
    public async Task 日報が有れば日報未確定通知は出さない()
    {
        var notifier = new FakeUnconfirmedNotifier();

        // Hold で見送るが、これは日報未確定ではないため通知しない。
        await CreateWithNotifier("""{"action":"Hold","rationale":"様子見"}""", Policy, notifier).DecideAsync(Trigger());

        notifier.Calls.Should().Be(0);
    }

    // 既定挙動: notifier 未指定（NoOp）でも日報未確定の見送りは従来どおり null を返す（現行挙動を壊さない）。
    [Fact]
    public async Task notifier未指定でも日報未確定は従来どおり見送る()
    {
        (await Create(BuyJson, policy: null).DecideAsync(Trigger())).Should().BeNull();
    }

    // fail-safe: 通知発行が例外でも判断経路（見送り＝null 返却）を壊さない。
    [Fact]
    public async Task 通知発行が例外でも見送りは継続する()
    {
        (await CreateWithNotifier(BuyJson, policy: null, new ThrowingUnconfirmedNotifier())
            .DecideAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task Buy判断は発注意図をOpenで組み立てTradeDecisionMadeを返す()
    {
        var decision = await Create(BuyJson, Policy).DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.Side.Should().Be(TradeSide.Buy);
        decision.Intent.PositionEffect.Should().Be(PositionEffect.Open); // IADR-0004
        decision.Intent.Symbol.Should().Be("7203");
        decision.Rationale.Should().Be("押し目");
        decision.DecidedAt.Should().Be(Now);
        // IADR-0035: ロングの損切り価格＝参照価格 − 損切り幅（1,000 − 30 = 970）。
        decision.Intent.StopLossPrice.Should().Be(970m);
    }

    [Fact]
    public async Task 損切り幅が参照価格以上の異常値は取引しない()
    {
        // IADR-0035: 損切り価格が権威データとして下流へ渡るため、距離≥参照価格（ロング損切り≤0）は幻覚として Hold に倒す。
        const string badJson =
            """{"action":"Buy","rationale":"幻覚","referencePrice":1000,"stopLossDistancePerShare":1500}""";

        (await Create(badJson, Policy).DecideAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task Sell判断の損切り価格は参照価格より上に置かれる()
    {
        // IADR-0035: ショートは参照価格 + 損切り幅（1,000 + 30 = 1,030）。
        const string sellJson =
            """{"action":"Sell","rationale":"戻り売り","referencePrice":1000,"stopLossDistancePerShare":30}""";

        // #292, IADR-0119: 売り判断が新規建て（Open）になるのはショートへの建て増しのときだけ。
        // 保有なし・不明の売りは見送りへ倒れるため、既存ショート（-50）を与えてエントリー経路を通す。
        var decision = await CreateWithHeld(sellJson, new FakeHeld(-50)).DecideAsync(Trigger());

        decision!.Intent.Side.Should().Be(TradeSide.Sell);
        decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        decision.Intent.StopLossPrice.Should().Be(1_030m);
    }

    [Fact]
    public async Task 発注意図の数量は必ずPositionSizer経由で確定される()
    {
        // IADR-0003 結合: 数量が PositionSizer.CalculateCappedQuantity と一致し、availableCapital は
        // 段階残枠(50,000)と日次残枠(20,000)の小さい方(20,000)が採られる。
        var ctx = Context(stageRemaining: 50_000m, dailyRemaining: 20_000m);
        var expected = PositionSizer.CalculateCappedQuantity(
            capital: 100_000m,
            perTradeRiskRatio: ctx.Limits.PerTradeRiskRatio,
            stopLossDistancePerShare: 30m,
            referencePrice: 1_000m,
            maxOrderAmount: ctx.Limits.MaxOrderAmountFor(ctx.Capital),
            availableCapital: 20_000m, // min(50,000, 20,000)
            sizeFactor: 1m);

        var decision = await Create(BuyJson, Policy, ctx).DecideAsync(Trigger());

        expected.Should().Be(20); // floor(20,000/1,000)=20 が min(risk=33, amount=20) を決める
        decision!.Intent.Quantity.Should().Be(expected);
    }

    [Fact]
    public async Task 残枠ゼロなら数量ゼロで取引しない()
    {
        // 日次発注残枠 0 → availableCapital 0 → 数量 0 → 見送り。
        var decision = await Create(BuyJson, Policy, Context(dailyRemaining: 0m)).DecideAsync(Trigger());

        decision.Should().BeNull();
    }

    [Fact]
    public async Task 定時トリガーでも同一ロジックで判断する_合流()
    {
        // FR-02, IADR-0023: 価格変動なしの定時（Scheduled）トリガーでも DecideAsync が判断を行う。
        var scheduled = DecisionTrigger.Scheduled("7203", Market.Japan);

        var decision = await Create(BuyJson, Policy).DecideAsync(scheduled);

        decision.Should().NotBeNull();
        decision!.Intent.Symbol.Should().Be("7203");
        decision.Intent.PositionEffect.Should().Be(PositionEffect.Open);
    }

    // FR-08, IADR-0072 決定1/4/5: 取得ポートが結果を返すと本判断プロンプトに参考情報として渡り、ポートが呼ばれる。
    [Fact]
    public async Task RAG取得ポートの結果は本判断プロンプトに注入される()
    {
        var llm = new CapturingLlm(BuyJson);
        var retrieval = new FakeRetrieval(new[]
        {
            new RetrievedContext("押し目の根拠メモ", "直近の調整は一時的との見立て。", "kb://doc/9", 0.88d),
        });
        var service = new AppSvc(llm, new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance, retrieval);

        var decision = await service.DecideAsync(Trigger());

        decision.Should().NotBeNull();
        retrieval.Calls.Should().Be(1);
        llm.LastPrompt.Should().Contain("参考情報（ナレッジベース）");
        llm.LastPrompt.Should().Contain("押し目の根拠メモ");
    }

    // FR-08, IADR-0072 決定4: 取得ポート未指定（既定 NoOp）は RAG 未結線と等価＝参考情報節を出さない現行動作。
    [Fact]
    public async Task 取得ポート未指定なら参考情報節を出さず従来どおり判断する()
    {
        var llm = new CapturingLlm(BuyJson);
        var service = new AppSvc(llm, new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance);

        var decision = await service.DecideAsync(Trigger());

        decision.Should().NotBeNull();
        llm.LastPrompt.Should().NotContain("参考情報（ナレッジベース）");
    }

    // FR-08, IADR-0072 決定4: 取得ポートが例外を投げても判断は止めず「文脈なし」に縮退して継続する（fail-safe）。
    [Fact]
    public async Task RAG取得が例外でも判断を止めず文脈なしで継続する()
    {
        var llm = new CapturingLlm(BuyJson);
        var service = new AppSvc(llm, new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance, new ThrowingRetrieval());

        var decision = await service.DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.Side.Should().Be(TradeSide.Buy);
        llm.LastPrompt.Should().NotContain("参考情報（ナレッジベース）");
    }

    // --- FR-17, IADR-0076: 採算評価ゲート（opt-in・fail-safe）の検証 ---

    private sealed class FakeProfitability(TradeCostAssessment? assessment) : IProfitabilityAssumptionsProvider
    {
        public int Calls { get; private set; }
        public decimal LastNotional { get; private set; }

        public Task<TradeCostAssessment?> AssessAsync(Market market, decimal notional, CancellationToken ct = default)
        {
            Calls++;
            LastNotional = notional;
            return Task.FromResult(assessment);
        }
    }

    // BuyJson（数量 20・約定代金 20,000）で採算成立する想定利益。gross = 10 × 20 = 200 ≥ (100+0)×1.5 = 150。
    private const string BuyJsonProfitable =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30,"expectedProfitPerShare":10}""";

    // 採算不成立の想定利益。gross = 5 × 20 = 100 < 150。
    private const string BuyJsonThinProfit =
        """{"action":"Buy","rationale":"薄利","referencePrice":1000,"stopLossDistancePerShare":30,"expectedProfitPerShare":5}""";

    private static AppSvc CreateWithGate(
        string llmOutput, IProfitabilityAssumptionsProvider prof, ProfitabilityGateOptions opts) =>
        new(new FakeLlm(llmOutput), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: prof, profitabilityOptions: opts);

    // 基準6: ゲート無効（既定）は採算評価せず発注意図を作る（想定利益 0 でも約定）。プロバイダは呼ばれない。
    [Fact]
    public async Task 採算ゲート無効なら採算評価せず発注意図を作る()
    {
        var prof = new FakeProfitability(new TradeCostAssessment(100m, 1.5m, 3));

        var decision = await CreateWithGate(BuyJson, prof, ProfitabilityGateOptions.Default).DecideAsync(Trigger());

        decision.Should().NotBeNull();
        prof.Calls.Should().Be(0); // 無効時は費用見積りを取りに行かない
    }

    // 基準9: ゲート有効かつ採算成立なら従来どおり発注意図を作る。
    [Fact]
    public async Task 採算ゲート有効かつ採算成立なら発注意図を作る()
    {
        var prof = new FakeProfitability(new TradeCostAssessment(100m, 1.5m, 3));
        var opts = ProfitabilityGateOptions.Default with { Enabled = true };

        var decision = await CreateWithGate(BuyJsonProfitable, prof, opts).DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.Quantity.Should().Be(20);
        prof.Calls.Should().Be(1);
    }

    // 基準7: ゲート有効かつ採算不成立（想定利益がしきい値未満）なら見送り。
    [Fact]
    public async Task 採算ゲート有効かつ採算不成立なら見送り()
    {
        var prof = new FakeProfitability(new TradeCostAssessment(100m, 1.5m, 3));
        var opts = ProfitabilityGateOptions.Default with { Enabled = true };

        var decision = await CreateWithGate(BuyJsonThinProfit, prof, opts).DecideAsync(Trigger());

        decision.Should().BeNull();
    }

    // 基準8: ゲート有効かつ前提条件未解決（Assess=null）は費用見積り不能＝安全側で見送り（想定利益が十分でも）。
    [Fact]
    public async Task 採算ゲート有効かつ費用見積り不能なら見送り()
    {
        var prof = new FakeProfitability(assessment: null);
        var opts = ProfitabilityGateOptions.Default with { Enabled = true };

        var decision = await CreateWithGate(BuyJsonProfitable, prof, opts).DecideAsync(Trigger());

        decision.Should().BeNull();
    }

    // FR-17, IADR-0076 決定5: プロンプトへの採算節注入は opt-in に連動する。有効時のみ LLM へ渡すプロンプトに採算節が載る。
    [Fact]
    public async Task 採算ゲートの有効無効でプロンプトの採算節が切り替わる()
    {
        var prof = new FakeProfitability(new TradeCostAssessment(100m, 1.5m, 3));

        var enabledLlm = new CapturingLlm(BuyJsonProfitable);
        await new AppSvc(enabledLlm, new FakePolicy(Policy), new FakeSizing(Context()),
                new FakeClock(), NullLogger<AppSvc>.Instance,
                retrieval: null, options: null, profitability: prof,
                profitabilityOptions: ProfitabilityGateOptions.Default with { Enabled = true })
            .DecideAsync(Trigger());

        var disabledLlm = new CapturingLlm(BuyJson);
        await new AppSvc(disabledLlm, new FakePolicy(Policy), new FakeSizing(Context()),
                new FakeClock(), NullLogger<AppSvc>.Instance,
                retrieval: null, options: null, profitability: prof,
                profitabilityOptions: ProfitabilityGateOptions.Default)
            .DecideAsync(Trigger());

        enabledLlm.LastPrompt.Should().Contain("採算評価（費用控除後の期待利益）");
        disabledLlm.LastPrompt.Should().NotContain("採算評価（費用控除後の期待利益）");
    }

    [Fact]
    public async Task 連敗時は縮小係数が数量に反映される()
    {
        // FR-10, #329, ADR-0018: GetSizeFactor は連敗しきい値(5)以上で半減。数量が縮小される。
        var ctx = Context(losses: 5);
        var expected = PositionSizer.CalculateCappedQuantity(
            100_000m, ctx.Limits.PerTradeRiskRatio, 30m, 1_000m, ctx.Limits.MaxOrderAmountFor(ctx.Capital),
            20_000m, PositionSizer.GetSizeFactor(5, 0m, ctx.Limits));

        var decision = await Create(BuyJson, Policy, ctx).DecideAsync(Trigger());

        decision!.Intent.Quantity.Should().Be(expected);
    }

    // --- FR-02, FR-04, FR-10, IADR-0099: 現在値（価格文脈）供給と権威価格アンカリングの検証 ---

    private sealed class FakeCurrentPrice(decimal? price, bool enabled = true) : ICurrentPriceProvider
    {
        public int Calls { get; private set; }
        public bool IsEnabled => enabled;

        public Task<decimal?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(price);
        }
    }

    private sealed class ThrowingCurrentPrice(bool enabled = true) : ICurrentPriceProvider
    {
        public bool IsEnabled => enabled;

        public Task<decimal?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            throw new InvalidOperationException("現在値取得の擬似障害");
    }

    private static AppSvc CreateWithPrice(string llmOutput, ICurrentPriceProvider price, SizingContext? ctx = null) =>
        new(new FakeLlm(llmOutput), new FakePolicy(Policy), new FakeSizing(ctx ?? Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance, currentPrice: price);

    // 現在値ありのとき、サイジング・OrderIntent・損切り価格は LLM の referencePrice ではなく権威ある現在値を用いる。
    [Fact]
    public async Task 現在値ありなら参照価格を権威価格へアンカリングする()
    {
        // 現在値 1,200（LLM の referencePrice 1,000 は使わない）。損切り価格＝1,200 − 30 = 1,170（IADR-0035）。
        var ctx = Context();
        var expectedQty = PositionSizer.CalculateCappedQuantity(
            100_000m, ctx.Limits.PerTradeRiskRatio, 30m, 1_200m, ctx.Limits.MaxOrderAmountFor(ctx.Capital), 20_000m, 1m);

        var decision = await CreateWithPrice(BuyJson, new FakeCurrentPrice(1_200m), ctx).DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.StopLossPrice.Should().Be(1_170m);
        decision.Intent.Quantity.Should().Be(expectedQty);
    }

    // fail-safe: 現在値ソースが有効化（IsEnabled=true）されているのに現在値が取れないなら発注抑止（見送り）。
    [Fact]
    public async Task 現在値ソース有効かつ現在値が取れないなら見送り()
    {
        (await CreateWithPrice(BuyJson, new FakeCurrentPrice(null, enabled: true)).DecideAsync(Trigger()))
            .Should().BeNull();
    }

    // 未有効化（IsEnabled=false・既定 no-op と等価）は現在値が無くても発注抑止せず従来どおり判断する（現行挙動）。
    [Fact]
    public async Task 現在値ソース無効なら現在値が無くても従来どおり判断する()
    {
        var decision = await CreateWithPrice(BuyJson, new FakeCurrentPrice(null, enabled: false)).DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.StopLossPrice.Should().Be(970m); // LLM 参照価格 1,000 − 30
    }

    // fail-safe: 現在値取得が例外でも判断は止めず、有効化時は安全側（見送り）へ倒す。
    [Fact]
    public async Task 現在値取得が例外_有効なら安全側で見送り()
    {
        (await CreateWithPrice(BuyJson, new ThrowingCurrentPrice(enabled: true)).DecideAsync(Trigger()))
            .Should().BeNull();
    }

    // fail-safe: 現在値取得が例外でも未有効化なら現行どおり継続する（現在値なしに縮退）。
    [Fact]
    public async Task 現在値取得が例外_無効なら現行どおり継続する()
    {
        var decision = await CreateWithPrice(BuyJson, new ThrowingCurrentPrice(enabled: false)).DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.StopLossPrice.Should().Be(970m); // 現在値なし → LLM 参照価格でアンカリング
    }

    // アンカリング後、損切り幅が権威価格以上（損切り価格≤0）になる異常は権威価格に対する不変量違反として見送り（IADR-0035）。
    [Fact]
    public async Task アンカリング後に損切り幅が現在値以上なら見送り()
    {
        // 現在値 25 に対し損切り幅 30（≥現在値）。
        (await CreateWithPrice(BuyJson, new FakeCurrentPrice(25m)).DecideAsync(Trigger()))
            .Should().BeNull();
    }

    // 定時トリガー（価格を持たない）でも、現在値が供給されればプロンプトに載り LLM が Buy できる。
    [Fact]
    public async Task 定時トリガーでも現在値ありならプロンプトに現在値を載せる()
    {
        var llm = new CapturingLlm(BuyJson);
        var service = new AppSvc(llm, new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance, currentPrice: new FakeCurrentPrice(1_200m));

        var decision = await service.DecideAsync(DecisionTrigger.Scheduled("7203", Market.Japan));

        decision.Should().NotBeNull();
        llm.LastPrompt.Should().Contain("定時サイクル（価格変動トリガーなし）");
        llm.LastPrompt.Should().Contain("現在値: 1200");
    }

    // 現在値未供給（既定 no-op）なら定時プロンプトに現在値行を出さない＝現行動作。
    [Fact]
    public async Task 定時トリガーで現在値未供給ならプロンプトに現在値行を出さない()
    {
        var llm = new CapturingLlm(BuyJson);
        var service = new AppSvc(llm, new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance);

        await service.DecideAsync(DecisionTrigger.Scheduled("7203", Market.Japan));

        llm.LastPrompt.Should().Contain("定時サイクル（価格変動トリガーなし）");
        llm.LastPrompt.Should().NotContain("現在値");
    }

    // 採算ゲート有効時の notional はアンカリング済みの参照価格（現在値）× 数量で算出される。
    [Fact]
    public async Task 採算ゲート有効時のnotionalは権威価格由来()
    {
        var prof = new FakeProfitability(new TradeCostAssessment(100m, 1.5m, 3));
        var opts = ProfitabilityGateOptions.Default with { Enabled = true };
        var service = new AppSvc(new FakeLlm(BuyJsonProfitable), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: prof, profitabilityOptions: opts,
            unconfirmedNotifier: null, currentPrice: new FakeCurrentPrice(1_200m));

        await service.DecideAsync(Trigger());

        // 数量＝floor(min(50,000,20,000)/1,200)=16。notional＝1,200 × 16 = 19,200。
        prof.Calls.Should().Be(1);
        prof.LastNotional.Should().Be(19_200m);
    }

    // #247, FR-04, FR-11, IADR-0104: LLM 拒否に由来する Hold は発注意図を作らず、その理由が監査ログへ到達する
    // （Hold は TradeDecisionMade を発行しないため、この FR-11 ログ 1 行が唯一の記録である）。
    [Fact]
    public async Task 拒否由来のHoldは発注せず拒否の理由が監査ログに残る()
    {
        var logger = new RecordingLogger();
        var refusedHold = """{"action":"Hold","rationale":"LLM が要求を拒否したため見送り"}""";
        var service = new AppSvc(new FakeLlm(refusedHold), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), logger);

        var result = await service.DecideAsync(Trigger());

        result.Should().BeNull(); // 発注意図を作らない
        string.Join("\n", logger.Messages).Should().Contain("LLM が要求を拒否したため見送り");
    }

    // --- FR-10, FR-17, #257, IADR-0107: 通貨換算（基準通貨 JPY）の検証 ---

    private sealed class FakeFxRate(decimal? rate) : IFxRateProvider
    {
        public int Calls { get; private set; }

        public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(rate);
        }
    }

    private sealed class ThrowingFxRate : IFxRateProvider
    {
        public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default) =>
            throw new InvalidOperationException("為替レート取得の擬似障害");
    }

    // AAPL 336.77 USD 相当。現在値は権威価格として供給し、LLM の referencePrice は使わない。
    // 損切り幅・想定利益はローカル通貨（USD）建てで返る前提（プロンプトでもその旨を明示している）。
    private const string UsdBuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":336.77,"stopLossDistancePerShare":0.5,"expectedProfitPerShare":20}""";

    private static PriceMovementDetected UsdTrigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 336.77m, 320m, 0.05m, Now);

    private static AppSvc CreateForCurrency(
        IFxRateProvider fx, ICurrentPriceProvider? price = null, ILogger<AppSvc>? logger = null,
        IProfitabilityAssumptionsProvider? prof = null, ProfitabilityGateOptions? profOpts = null) =>
        new(new FakeLlm(UsdBuyJson), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), logger ?? NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: prof, profitabilityOptions: profOpts,
            unconfirmedNotifier: null, currentPrice: price, fxRate: fx);

    // 基準4: 非基準通貨でレートが解決できなければ新規建てを見送る（過大発注を招かない安全側）。
    [Fact]
    public async Task 外貨建て銘柄で換算レートが無ければ見送る()
    {
        var logger = new RecordingLogger();

        var decision = await CreateForCurrency(new FakeFxRate(null), logger: logger).DecideAsync(UsdTrigger());

        decision.Should().BeNull();
        string.Join("\n", logger.Messages).Should().Contain("基準通貨への換算レートが解決できないため見送り");
    }

    // 既定（実 FX 未結線）でも外貨建て銘柄は見送りになる＝統制が効かない状態で発注しない。
    [Fact]
    public async Task FX未結線の既定では外貨建て銘柄を見送る()
    {
        var service = new AppSvc(new FakeLlm(UsdBuyJson), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance);

        (await service.DecideAsync(UsdTrigger())).Should().BeNull();
    }

    // fail-safe: レート取得の例外はレート無しへ縮退し、見送りになる（例外は安全側に働く）。
    [Fact]
    public async Task 換算レート取得が例外なら見送る()
    {
        (await CreateForCurrency(new ThrowingFxRate()).DecideAsync(UsdTrigger())).Should().BeNull();
    }

    // 基準7: 基準通貨（日本株）は実 FX 源を結線しなくても従来どおり発注意図を作る（影響を非基準通貨に限定する）。
    // 「基準通貨では外部レート源へ問い合わせない」ことは源側で検証する
    //（FredFxRateSourceTests.基準通貨は外部へ問い合わせずレート1を返す＝送信 0 件）。
    [Fact]
    public async Task 基準通貨の市場は実FX源が無くても従来どおり判断する()
    {
        var service = new AppSvc(new FakeLlm(BuyJson), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            retrieval: null, options: null, profitability: null, profitabilityOptions: null,
            unconfirmedNotifier: null, currentPrice: null,
            fxRate: new BaseCurrencyOnlyFxRateProvider());

        var decision = await service.DecideAsync(Trigger());

        decision.Should().NotBeNull();
        decision!.Intent.FxRateToBase.Should().Be(1m, "基準通貨の市場は定義上レート 1（換算しない）");
    }

    // 基準5/6: レートありなら換算後の金額でサイジングし、発注意図の価格はローカル通貨のまま載せる。
    [Fact]
    public async Task 外貨建て銘柄は換算後の金額でサイジングし価格はローカル通貨のまま載せる()
    {
        // 336.77 USD × 150 円 = 50,515.5 円/株。1 注文金額上限 35,000 円では 0 株となるため、
        // ここでは上限に収まる低位株（20 USD = 3,000 円）で「換算後の金額でキャップされる」ことを確認する。
        var decision = await CreateForCurrency(new FakeFxRate(150m), new FakeCurrentPrice(20m))
            .DecideAsync(UsdTrigger());

        decision.Should().NotBeNull();
        // 金額基準（円換算）: min(35,000, min(50,000, 20,000)) ÷ (20 USD × 150) = 6 株。
        // リスク予算基準: 100,000 × 1% ÷ (0.5 USD × 150) = 13 株。小さい方の 6 株を採る。
        // 換算しなければ金額基準は 20,000 ÷ 20 = 1,000 株（＝約 300 万円相当）と桁で誤る。
        decision!.Intent.Quantity.Should().Be(6);
        decision.Intent.Price.Should().Be(20m, "発注執行へ渡す価格はローカル通貨（USD）のまま");
        decision.Intent.StopLossPrice.Should().Be(19.5m, "損切り価格もローカル通貨（20 USD − 0.5 USD）");
        decision.Intent.FxRateToBase.Should().Be(150m);
        decision.Intent.NotionalInBase.Should().Be(18_000m); // 6 × 20 USD × 150
    }

    // 基準3 の判断側の対: 換算後の金額が 1 注文金額上限を超える高価格株は数量 0＝見送りになる（正しい帰結）。
    [Fact]
    public async Task 換算後に1注文金額上限を超える高価格株は見送る()
    {
        var decision = await CreateForCurrency(new FakeFxRate(150m), new FakeCurrentPrice(336.77m))
            .DecideAsync(UsdTrigger());

        decision.Should().BeNull("336.77 USD × 150 円 ≒ 50,516 円/株 は 1 注文金額上限 35,000 円を超える");
    }

    // 基準8: 採算評価の notional・想定利益は基準通貨（円）で突き合わせる（費用見積りの単位と揃える）。
    [Fact]
    public async Task 採算評価のnotionalは基準通貨で突き合わせる()
    {
        var prof = new FakeProfitability(new TradeCostAssessment(100m, 1.5m, 3));
        var opts = ProfitabilityGateOptions.Default with { Enabled = true };

        var decision = await CreateForCurrency(
                new FakeFxRate(150m), new FakeCurrentPrice(20m), prof: prof, profOpts: opts)
            .DecideAsync(UsdTrigger());

        decision.Should().NotBeNull();
        prof.Calls.Should().Be(1);
        // 6 株 × 20 USD × 150 円 = 18,000 円（USD のまま渡すと 120 円相当で費用と桁が合わない）。
        prof.LastNotional.Should().Be(18_000m);
    }

    private sealed class RecordingLogger : ILogger<AppSvc>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    // --- #292, IADR-0119: 判断由来の決済（AI の出口） ---

    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";

    [Fact]
    public async Task ロング保有への売り判断は全量の決済を発行する()
    {
        // 従来は PositionEffect.Open 固定で、保有ロングの売却が「新規ショート建て」として扱われていた。
        var decision = await CreateWithHeld(SellJson, new FakeHeld(4072)).DecideAsync(Trigger());

        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Side.Should().Be(TradeSide.Sell);
        decision.Intent.Quantity.Should().Be(4072, "出口の数量は保有数であってサイジング結果ではない");
        decision.Intent.StopLossPrice.Should().BeNull("決済注文に損切り価格は無い");
    }

    [Fact]
    public async Task ショート保有への買い判断は全量の決済を発行する()
    {
        const string buyJson =
            """{"action":"Buy","rationale":"買い戻し","referencePrice":1000,"stopLossDistancePerShare":30}""";

        var decision = await CreateWithHeld(buyJson, new FakeHeld(-100)).DecideAsync(Trigger());

        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Side.Should().Be(TradeSide.Buy);
        decision.Intent.Quantity.Should().Be(100);
    }

    [Fact]
    public async Task ロング保有への買い判断は従来どおり新規建てになる()
    {
        var decision = await CreateWithHeld(BuyJson, new FakeHeld(100)).DecideAsync(Trigger());

        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        decision.Intent.StopLossPrice.Should().Be(970m);
    }

    [Fact]
    public async Task 保有なしの売り判断は発注しない()
    {
        // 裸の新規ショート建て。現物のみ有効な段階では成立せず、取引ガードは方向を見ないため素通りしてしまう。
        (await CreateWithHeld(SellJson, new FakeHeld(0)).DecideAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task 建玉が不明な売り判断は発注しない()
    {
        // 既定（NoOpHeldPositionProvider）は常に不明。ADR-0003「不確実なら Hold」。
        (await Create(SellJson, Policy).DecideAsync(Trigger())).Should().BeNull();
    }

    [Fact]
    public async Task 建玉照会が失敗しても買い判断は従来どおり成立する()
    {
        // fail-safe: 照会例外は「不明」に縮退する。買いは裸になり得ず、金額系上限がそのまま効く。
        var service = new AppSvc(
            new FakeLlm(BuyJson), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance, heldPosition: new ThrowingHeld());

        var decision = await service.DecideAsync(Trigger());

        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
    }

    [Fact]
    public async Task 決済はサイジングの残枠に妨げられない()
    {
        // 残枠 0 は「新規建てできない」であって「手仕舞いできない」ではない（FR-10）。
        var noRoom = Context(stageRemaining: 0m, dailyRemaining: 0m);

        var decision = await CreateWithHeld(SellJson, new FakeHeld(4072), noRoom).DecideAsync(Trigger());

        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Quantity.Should().Be(4072);
    }

    [Fact]
    public async Task 決済は採算ゲートに妨げられない()
    {
        // IADR-0076 の最小期待利益で撤退を止めてはならない（損失を止めるための決済が通らなくなる）。
        // 採算ゲートを有効化し、費用見積り不能（NoOp）＝新規建てなら必ず見送りになる条件で決済が通ることを固定する。
        var service = new AppSvc(
            new FakeLlm(SellJson), new FakePolicy(Policy), new FakeSizing(Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            profitabilityOptions: new ProfitabilityGateOptions { Enabled = true },
            heldPosition: new FakeHeld(4072));

        var decision = await service.DecideAsync(Trigger());

        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
    }
}
