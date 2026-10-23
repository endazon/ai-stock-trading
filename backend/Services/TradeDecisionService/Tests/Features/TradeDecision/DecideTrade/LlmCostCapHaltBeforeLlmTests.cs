extern alias RiskManagementWorker;

using System.Net;
using System.Text;
using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 NFR-13, FR-02, UC-01, UC-02, #1423 M-5, ADR-0074 決定1・2, IADR-0582:
// **月次 LLM 費用上限の 100% に達したら、定時・価格変動の両方の判断を LLM の前で見送る**（新規建ても保有中の銘柄の出口の判断も）。
// 是正前は費用統制を読むのが情報収集の巡回だけで、価格変動トリガーの判断は 100% 到達後も LLM を呼び続けていた。
// 照会の失敗は「停止していない」へ倒す（IADR-0031 と同じ判断）。
public class LlmCostCapHaltBeforeLlmTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 13), "方針");

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class CountingLlm : ILlmCompletionClient
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult("""{"action":"Hold","rationale":"様子見","referencePrice":0}""");
        }
    }

    private sealed class CountingPolicy : IDailyPolicyProvider
    {
        public int Calls { get; private set; }

        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<DailyPolicy?>(Policy);
        }
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(
            new SizingContext(100_000m, 50_000m, 50_000m, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits()));
    }

    private sealed class FixedGate(bool halted) : ILlmCostHaltGate
    {
        public Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default) => Task.FromResult(halted);
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

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<(string Trigger, DecisionSkipReason Reason)> Reports { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reports.Add((trigger, reason));
    }

    private sealed record Probe(AppSvc Service, CountingLlm Llm, CountingPolicy Policy, RecordingForgone Forgone, RecordingSkips Skips);

    private static Probe Create(ILlmCostHaltGate? gate)
    {
        var llm = new CountingLlm();
        var policy = new CountingPolicy();
        var forgone = new RecordingForgone();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, policy, new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            skipReporter: skips, forgoneReporter: forgone, costHalt: gate);
        return new Probe(service, llm, policy, forgone, skips);
    }

    private static DecisionTrigger Scheduled() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    private static DecisionTrigger PriceMovement() => DecisionTrigger.FromPriceMovement(
        new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 20.80m, 20.00m, 0.04m, Now));

    // 🔴 T-10-3049: 停止中は**価格変動・定時の両方**で LLM を呼ばず見送り、理由 LlmCostCapHalted を台帳（見送りの記録）と計数に残す。
    [Theory]
    [InlineData("price-movement")]
    [InlineData("scheduled")]
    public async Task T_10_3049_費用上限の停止中は定時も価格変動もLLMの前で見送る(string kind)
    {
        var probe = Create(new FixedGate(halted: true));
        var trigger = kind == "scheduled" ? Scheduled() : PriceMovement();

        var decision = await probe.Service.DecideAsync(trigger, TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        probe.Llm.Calls.Should().Be(0, "100% 到達の後は LLM を呼ばない（ADR-0074 決定2）");
        probe.Policy.Calls.Should().Be(0, "合流点の先頭で見送る（方針の照会も要らない）");
        var forgone = probe.Forgone.Reports.Should().ContainSingle().Which;
        forgone.Reason.Should().Be(DecisionForgoneBeforeLlmReason.LlmCostCapHalted);
        forgone.CycleTrigger.Should().Be(trigger.MetricTrigger);
        probe.Skips.Reports.Should().Equal((trigger.MetricTrigger, DecisionSkipReason.LlmCostCapHalted));
    }

    // T-10-3050（否定形）: 停止していない・未結線（本番の NoOp・引数の省略）なら従来どおり LLM を呼ぶ。
    [Theory]
    [InlineData("not-halted")]
    [InlineData("noop")]
    [InlineData("omitted")]
    public async Task T_10_3050_停止していなければ従来どおりLLMを呼ぶ(string wiring)
    {
        var probe = Create(wiring switch
        {
            "not-halted" => new FixedGate(false),
            "noop" => new NoOpLlmCostHaltGate(),
            _ => null,
        });

        await probe.Service.DecideAsync(PriceMovement(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0);
        probe.Forgone.Reports.Should().NotContain(r => r.Reason == DecisionForgoneBeforeLlmReason.LlmCostCapHalted);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond());
        }
    }

    private static HttpLlmCostHaltGate Gate(Func<HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new HttpLlmCostHaltGate(
            new HttpClient(handler) { BaseAddress = new Uri("http://cost-control-service:8080") },
            NullLogger<HttpLlmCostHaltGate>.Instance);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // T-10-3051（境界の表）: 費用統制の GET /costs/state の写し。停止は **isHalted が明示的に true のときだけ**。
    // 非 2xx・欠落・真偽でない値・オブジェクトでない本文は「停止していない」（判断を続ける）。
    [Theory]
    [InlineData("""{"state":"Halted","isHalted":true,"intervalMultiplier":0}""", HttpStatusCode.OK, true)]
    [InlineData("""{"ISHALTED":true}""", HttpStatusCode.OK, true)]
    [InlineData("""{"state":"Throttled","isHalted":false,"intervalMultiplier":2}""", HttpStatusCode.OK, false)]
    [InlineData("""{"state":"Normal"}""", HttpStatusCode.OK, false)]
    [InlineData("""{"isHalted":"true"}""", HttpStatusCode.OK, false)]
    [InlineData("""[]""", HttpStatusCode.OK, false)]
    [InlineData("""{"isHalted":true}""", HttpStatusCode.Unauthorized, false)]
    [InlineData("""{"isHalted":true}""", HttpStatusCode.InternalServerError, false)]
    public async Task T_10_3051_費用統制の応答はisHaltedが明示的にtrueのときだけ停止と読む(
        string body, HttpStatusCode status, bool expected)
    {
        var gate = Gate(() => Json(body, status), out var handler);

        (await gate.IsHaltedAsync(TestContext.Current.CancellationToken)).Should().Be(expected);
        handler.Paths.Should().Equal("/costs/state");
    }

    // T-10-3051（続き）: 照会の例外は「停止していない」（費用統制の一時障害で取引判断全体を止めない。IADR-0031）。
    [Fact]
    public async Task T_10_3051_照会の例外は停止していないと読む()
    {
        var gate = Gate(() => throw new HttpRequestException("cost-control down"), out _);

        (await gate.IsHaltedAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
