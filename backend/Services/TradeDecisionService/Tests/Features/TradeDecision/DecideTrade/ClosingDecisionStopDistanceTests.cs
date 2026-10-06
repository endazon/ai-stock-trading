extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// T-10-2285, FR-04, FR-10, FR-11, ADR-0003, #1187, IADR-0248, IADR-0119: 判断サービスの端から端で、保有を決済する売買の損切り幅を任意にする。
// PoC 2026-10-06: 保有 970 株の AMZN で、利確の Sell が損切り幅を省いて返り、二次本判断の解釈が 14 回 InvalidValues→Hold に倒した。
// 決済の経路は保有全量・損切り価格なしで損切り幅を読まない。新規建ては従来どおり損切り幅が必須。実 LLM は呼ばない。
public class ClosingDecisionStopDistanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 14, 51, 42, TimeSpan.Zero);

    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 6), "利確: 全銘柄 +3%。");

    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private const string TakeProfitSellWithoutStop =
        """{"action":"Sell","rationale":"含み益+3.04%で方針の利確基準(+3%)に到達","referencePrice":255.55,"stopLossDistancePerShare":null}""";

    private const string EntryBuyWithoutStop =
        """{"action":"Buy","rationale":"押し目","referencePrice":255.55,"stopLossDistancePerShare":null}""";

    private static DecisionTrigger Trigger() => DecisionTrigger.Scheduled("AMZN", Market.UnitedStates, Now);

    private static AppSvc Service(string llmOutput, IHeldPositionProvider held, RecordingSkips? skips = null) =>
        new(new FixedLlm(llmOutput), new FakePolicy(), new FakeSizing(), new FakeClock(),
            NullLogger<AppSvc>.Instance, heldPosition: held, skipReporter: skips);

    // 🔴 本件の核: 保有中（ロング 970 株）の利確の Sell は、損切り幅が null でも決済の発注意図（保有全量・Close・損切り価格なし）になる。
    [Fact]
    public async Task 保有中の利確のSellは損切り幅がnullでも保有全量の決済になる()
    {
        var made = await Service(TakeProfitSellWithoutStop, new Held(before: 970, after: 970)).DecideAsync(Trigger());

        made.Should().NotBeNull("決済は損切り幅を使わない（#1187）");
        made!.Intent.Side.Should().Be(TradeSide.Sell);
        made.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        made.Intent.Quantity.Should().Be(970, "決済の数量は保有全量");
        made.Intent.StopLossPrice.Should().BeNull();
        made.StopWidth.Should().BeNull("決済は損切りラインを作らない");
    }

    // 否定形: 保有 0 の Sell は、損切り幅が null なら従来どおり解析不能（Hold 票）＝見送り（緩和は決済だけに掛かる）。
    [Fact]
    public async Task 保有0のSellは損切り幅がnullなら従来どおり解析不能で見送る_否定形()
    {
        var skips = new RecordingSkips();

        var made = await Service(TakeProfitSellWithoutStop, new Held(before: 0, after: 0), skips).DecideAsync(Trigger());

        made.Should().BeNull();
        skips.Reasons.Should().Equal(DecisionSkipReason.LlmHold);
    }

    // 否定形: 新規建ての Buy（保有 0）は損切り幅が null なら従来どおり InvalidValues→Hold＝見送り。
    [Fact]
    public async Task 新規建てのBuyは損切り幅がnullなら従来どおり見送る_否定形()
    {
        var skips = new RecordingSkips();

        var made = await Service(EntryBuyWithoutStop, new Held(before: 0, after: 0), skips).DecideAsync(Trigger());

        made.Should().BeNull();
        skips.Reasons.Should().Equal(DecisionSkipReason.LlmHold);
    }

    // 否定形: 判断の前は保有中（緩和が掛かる）でも、LLM の後の引き直しで保有 0（逆指値が約定した等）なら発注しない。
    // 未使用の印 0 の損切り幅が新規建てへ流れる経路が無いことを固定する（裸の新規売りとして見送る）。
    [Fact]
    public async Task 判断前は保有中でも引き直しで保有0なら発注しない_否定形()
    {
        var skips = new RecordingSkips();

        var made = await Service(TakeProfitSellWithoutStop, new Held(before: 970, after: 0), skips).DecideAsync(Trigger());

        made.Should().BeNull();
        skips.Reasons.Should().Equal(DecisionSkipReason.NakedShortOpen);
    }

    // 否定形: 判断の前は保有中のロング（緩和が掛かる）でも、引き直しでショート（売り増し＝新規建て）になったら、未使用の印 0 の
    // 損切り幅は新規建ての再検証で必ず落ちる（損切り幅なしの新規建ては通らない）。
    [Fact]
    public async Task 引き直しで新規建てになっても印0の損切り幅は再検証で落ちる_否定形()
    {
        var skips = new RecordingSkips();

        var made = await Service(TakeProfitSellWithoutStop, new Held(before: 970, after: -10), skips).DecideAsync(Trigger());

        made.Should().BeNull();
        skips.Reasons.Should().Equal(DecisionSkipReason.StopLossDistanceInvalid);
    }

    // ------------------------------------------------------------------------------------------------

    // 判断の前（プロンプト・解釈に使う保有）と後（建玉効果の引き直し）で違う保有を返せる照会口（実結線扱い）。
    private sealed class Held(int before, int after) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(after);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<HeldPosition?>(before == 0 ? HeldPosition.None : new HeldPosition(before, 248.015m, null));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(Context);
    }
}
