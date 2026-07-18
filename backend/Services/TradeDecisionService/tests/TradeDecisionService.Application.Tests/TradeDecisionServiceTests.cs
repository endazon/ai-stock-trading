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

        public Task<TradeCostAssessment?> AssessAsync(Market market, decimal notional, CancellationToken ct = default)
        {
            Calls++;
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
}
