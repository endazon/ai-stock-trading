using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
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

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");

    private static SizingContext Context(decimal stageRemaining = 50_000m, decimal dailyRemaining = 20_000m,
        int losses = 0, decimal dd = 0m) =>
        new(100_000m, stageRemaining, dailyRemaining, losses, dd, TradeMode.Paper, TradingDefaults.CreateRiskLimits());

    private static AppSvc Create(string llmOutput, DailyPolicy? policy, SizingContext? ctx = null) =>
        new(new FakeLlm(llmOutput), new FakePolicy(policy), new FakeSizing(ctx ?? Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance);

    private static PriceMovementDetected Trigger() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now);

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
        decision.Intent.Symbol.Should().Be("AAPL");
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

        var decision = await Create(sellJson, Policy).DecideAsync(Trigger());

        decision!.Intent.Side.Should().Be(TradeSide.Sell);
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
            maxOrderAmount: ctx.Limits.MaxOrderAmount,
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
        var scheduled = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var decision = await Create(BuyJson, Policy).DecideAsync(scheduled);

        decision.Should().NotBeNull();
        decision!.Intent.Symbol.Should().Be("AAPL");
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
        // GetSizeFactor: 連敗しきい値(3)以上で半減。数量が縮小される。
        var ctx = Context(losses: 3);
        var expected = PositionSizer.CalculateCappedQuantity(
            100_000m, ctx.Limits.PerTradeRiskRatio, 30m, 1_000m, ctx.Limits.MaxOrderAmount,
            20_000m, PositionSizer.GetSizeFactor(3, 0m, ctx.Limits));

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
            100_000m, ctx.Limits.PerTradeRiskRatio, 30m, 1_200m, ctx.Limits.MaxOrderAmount, 20_000m, 1m);

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

        var decision = await service.DecideAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates));

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

        await service.DecideAsync(DecisionTrigger.Scheduled("AAPL", Market.UnitedStates));

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
}
