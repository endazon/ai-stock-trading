extern alias RiskManagementWorker;

using System.Globalization;
using AiStockTrading.Shared.Contracts.Backtest;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// 🔴 FR-10, FR-15, #1176, IADR-0495 決定1, #1209, IADR-0507: Stage 0 の記録器は、本番がサイジングの直後に SizedBelowMinimumNotional で見送る判断に
// 「新規建てとして最小の名目額に満たない」（EntryBelowMinimumNotional = true）を記録する。判定は本番と同じ関数・同じしきい値の構成。
// 数量は 0 にしない（再生で決済として働くことがあるため。適用は再生側が新規建てにだけ行う）。
public class Stage0MinimumEntryNotionalTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 29);
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.Zero);
    private const string TestModel = "stage0-test-model";

    private static SizingContext Sizing(decimal equity, decimal stageRemaining, decimal dailyRemaining = 1_000_000m) =>
        new(equity, stageRemaining, dailyRemaining, 0, 0m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static string DecisionJson(string action, string price, string stop) =>
        $$"""{"action":"{{action}}","rationale":"根拠","referencePrice":{{price}},"stopLossDistancePerShare":{{stop}}}""";

    // T-10-2412: 境界（equity 100,000 の 1%＝1,000・残枠 1,500 で 1 株だけ買える）。999.99 は true、ちょうど 1,000 と 1,000.01 は false。
    // 数量は 0 にしない（判定だけを残す）。売り（記録器では新規の売り）も同じ判定。
    [Theory]
    [InlineData("Buy", "999.99", true, 1)]
    [InlineData("Buy", "1000", false, 1)]
    [InlineData("Buy", "1000.01", false, 1)]
    [InlineData("Sell", "999.99", true, -1)]
    public async Task T_10_2412_記録器は新規建てとして最小の名目額に満たない判断に判定を残し数量は消さない(
        string action, string price, bool expected, int expectedQuantity)
    {
        var record = await RecordAsync(Sizing(100_000m, 1_500m), price, DecisionJson(action, price, "30"));

        record.EntryBelowMinimumNotional.Should().Be(expected);
        record.SignedQuantity.Should().Be(expectedQuantity, "数量は再生が建玉を見て扱う（記録器は保有を知らない）");
    }

    // T-10-2412: Hold と数量 0（不変量違反）は判定しない（null）。しきい値 0 は統制を外す（false）。構成を渡さなければ既定 1% で効く。
    [Fact]
    public async Task T_10_2412_Holdと数量0は判定せずしきい値0は外し構成なしは既定で効く()
    {
        (await RecordAsync(Sizing(100_000m, 1_500m), "999.99", """{"action":"Hold","rationale":"様子見"}"""))
            .EntryBelowMinimumNotional.Should().BeNull();
        (await RecordAsync(Sizing(100_000m, 1_500m), "999.99", DecisionJson("Buy", "999.99", "999.99")))
            .Should().Match<Stage0DecisionRecord>(r => r.SignedQuantity == 0 && r.EntryBelowMinimumNotional == null);
        (await RecordAsync(Sizing(100_000m, 1_500m), "999.99", DecisionJson("Buy", "999.99", "30"), new MinimumEntryNotionalOptions(0m)))
            .EntryBelowMinimumNotional.Should().BeFalse();
        (await RecordAsync(Sizing(100_000m, 1_500m), "999.99", DecisionJson("Buy", "999.99", "30"), options: null))
            .EntryBelowMinimumNotional.Should().BeTrue("既定 1%（TradingDefaults.MinEntryNotionalRatio）");
    }

    // T-10-2413: 🔴 本番と同値。同じ資金・残枠・価格・AI の幅を本番の判断（TradeDecisionAppService・保有 0）と記録器に通し、
    // 本番が SizedBelowMinimumNotional で見送る判断に限って記録器が true を残し、本番が発注する判断は記録器の数量が発注数量と一致する。
    [Theory]
    [InlineData("100000", "1500", "999.99", "30")]
    [InlineData("100000", "1500", "1000", "30")]
    [InlineData("100000", "1500", "1000.01", "30")]
    [InlineData("970000", "9800", "334.11", "10")] // #1176 の実例の形: 29 株＝9,689.19 ＜ 9,700
    [InlineData("100000", "50000", "100", "30")] // 1 取引リスクが効く大きな新規建て（33 株＝3,300 ≥ 1,000）
    [InlineData("100000", "50000", "100", "0.5")] // 幅は下限（2%）まで広げてからサイジング（500 株＝50,000 は残枠で 25,000 → 250 株）
    public async Task T_10_2413_記録器の判定は本番のサイジングの後の見送りと同値(string equity, string stage, string price, string stop)
    {
        var sizing = Sizing(D(equity), D(stage));
        var json = DecisionJson("Buy", price, stop);

        var skips = new List<DecisionSkipReason>();
        var production = new TradeDecisionAppService(
            new FixedLlm(json), new FixedPolicy(), new FixedSizing(sizing), new FixedClock(), NullLogger<TradeDecisionAppService>.Instance,
            currentPrice: new FixedCurrentPrice(D(price)), heldPosition: new Flat(), skipReporter: new SkipSink(skips),
            minimumEntryNotional: MinimumEntryNotionalOptions.Default);
        var made = await production.DecideAsync(
            DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now), TestContext.Current.CancellationToken);

        var record = await RecordAsync(sizing, price, json);

        var productionSkipsBelowMinimum = skips.Contains(DecisionSkipReason.SizedBelowMinimumNotional);
        record.EntryBelowMinimumNotional.Should().Be(productionSkipsBelowMinimum);
        if (made is not null)
            record.SignedQuantity.Should().Be(made.Intent.Quantity, "本番と同じサイジング");
        else
            productionSkipsBelowMinimum.Should().BeTrue("比べる場面は本番がサイジングまで進むものに限る");
    }

    // T-10-2567, FR-10, ADR-0063 決定1・決定2, #1291, IADR-0527 決定1: 記録器のサイジングも高ボラティリティ銘柄の 1 注文上限を本番と同じ関数で掛ける。
    // 明示指定の AAPL は equity 100,000 の 5%（5,000 ÷ 100 = 50 株）で切られ、指定が無ければ区分外の 25%（250 株）。残枠は上限より大きい。
    [Theory]
    [InlineData(true, 50)]
    [InlineData(false, 250)]
    public async Task T_10_2567_記録器は明示指定の銘柄の数量を5パーセントの上限で切る(bool designated, int expected)
    {
        var highVolatility = TradingDefaults.CreateHighVolatilitySettings() with
        {
            DesignatedSymbols = designated ? [new HighVolatilitySymbol("AAPL", Market.UnitedStates)] : [],
        };
        var sizing = Sizing(100_000m, 1_000_000m) with { HighVolatility = highVolatility };

        var record = await RecordAsync(sizing, "100", DecisionJson("Buy", "100", "0.5"));

        record.SignedQuantity.Should().Be(expected);
    }

    // ------------------------------------------------------------------------------------------------

    private static decimal D(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static async Task<Stage0DecisionRecord> RecordAsync(
        SizingContext sizing, string price, string decisionJson, MinimumEntryNotionalOptions? options = null)
    {
        var collector = new Stage0RecordingUsageCollector(new NullReporter());
        var sink = new CapturingSink();
        var input = new AsOfDecisionInput(AsOf, new DailyPolicy(AsOf, "当日の方針"), sizing, new DatedPrice(AsOf, D(price)));
        var recorder = new Stage0DecisionRecorder(
            new RecorderLlm(collector, decisionJson), DecisionOrchestrationOptions.Default, new FixedInputs(input), sink, collector,
            LlmPriceTable.From([(TestModel, "1", "1")], "1", "1"),
            new ManualTimeProvider(Now), NullLogger<Stage0DecisionRecorder>.Instance, options);

        var outcome = await recorder.RunAsync(Options(), TestContext.Current.CancellationToken);

        outcome.Status.Should().Be(Stage0RecordingStatus.Completed);
        return sink.Saved!.Records.Should().ContainSingle().Subject;
    }

    private static Stage0RecordingOptions Options() => new()
    {
        Enabled = true,
        From = "2026-09-29",
        To = "2026-09-29",
        LlmTrainingCutoff = "2026-03-31",
        Symbols = [new Stage0RecordingOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
        VoteCount = 1,
        DecisionsPerDay = 1,
        InputTokensPerDecision = 1_000,
        OutputTokensPerDecision = 1_000,
        ScreeningInputTokensPerDecision = 1_000,
        ScreeningOutputTokensPerDecision = 1_000,
        ApprovedVoteCount = 1,
        ApprovedEstimateJpy = 4m,
        OutputPath = "records.json",
        Model = TestModel,
    };

    private sealed class FixedInputs(AsOfDecisionInput input) : IAsOfDecisionInputProvider
    {
        public Task<AsOfDecisionInput?> GetAsync(
            string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult<AsOfDecisionInput?>(input);
    }

    // 記録器の二段: 一次は関心あり、本判断は与えた JSON。
    private sealed class RecorderLlm(Stage0RecordingUsageCollector usage, string decisionJson) : ILlmCompletionClient
    {
        public async Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken cancellationToken = default)
        {
            await usage.ReportAsync(new LlmUsage(purpose ?? LlmPurposes.TradeDecision, 1_000, 1_000, model), cancellationToken);
            return purpose == LlmPurposes.TradeDecisionScreening ? """{"action":"Buy","rationale":"関心あり"}""" : decisionJson;
        }
    }

    private sealed class CapturingSink : IStage0DecisionRecordSink
    {
        public Stage0DecisionRecordSet? Saved { get; private set; }

        public Task<bool> SaveAsync(Stage0DecisionRecordSet recordSet, CancellationToken cancellationToken = default)
        {
            Saved = recordSet;
            return Task.FromResult(true);
        }
    }

    private sealed class NullReporter : ILlmUsageReporter
    {
        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ---- 本番の判断の部品（保有 0・未約定なし・現在値は判断の参照価格と同じ） ----

    private sealed class FixedLlm(string output) : ILlmCompletionClient
    {
        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(output);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedPolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) =>
            Task.FromResult<DailyPolicy?>(new DailyPolicy(AsOf, "当日の方針"));
    }

    private sealed class FixedSizing(SizingContext context) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class FixedCurrentPrice(decimal price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult<CurrentPriceReading?>(new CurrentPriceReading(price, IntradayPriceContext.Unknown));
    }

    private sealed class Flat : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(0);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(HeldPosition.None);

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private sealed class SkipSink(List<DecisionSkipReason> reasons) : IDecisionSkipReporter
    {
        public void Report(string trigger, DecisionSkipReason reason) => reasons.Add(reason);
    }
}
