extern alias RiskManagementWorker;
extern alias ReportWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
using Proto = AiStockTrading.Shared.Grpc.Report.V1;
using ReportPolicy = ReportWorker::ReportService.Features.Reports.ConfirmedDailyPolicy;
using ReportWire = ReportWorker::ReportService.Features.Reports.DailyPolicyWireMapping;

namespace TradeDecisionService.Tests;

// 🔴 FR-07, UC-01, UC-02, UC-03, #1423 M-6, ADR-0075 決定1〜3, IADR-0583:
// **無応答時の「停止」が効いている間（報告書サービスが方針の照会に印を載せる）、判断の合流点は定時・価格変動の両方で取引判断の全体を見送る。**
// 方針は直近の確定済み日報のまま届く。日報未確定の通知（「確定済みの日報が無い」）は鳴らさない。
public class DailyReportNoResponseHaltTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

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

    private sealed class FixedPolicy(DailyPolicy policy) : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(
            new SizingContext(100_000m, 50_000m, 50_000m, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits()));
    }

    private sealed class CountingUnconfirmedNotifier : IDailyPolicyUnconfirmedNotifier
    {
        public int Calls { get; private set; }

        public Task NotifyAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
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

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private static DailyPolicy Policy(bool halted) =>
        new(new DateOnly(2026, 10, 12), "前日の確定済み方針", new DailyPolicyReference("daily-2026-10-12", 1), NoResponseHalted: halted);

    private static DecisionTrigger Scheduled() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    private static DecisionTrigger PriceMovement() => DecisionTrigger.FromPriceMovement(
        new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 20.80m, 20.00m, 0.04m, Now));

    // 🔴 T-10-3061: 印が立っていれば、定時・価格変動の両方で LLM を呼ばずに見送り、理由 DailyReportNoResponseHalted を記録する。
    // 日報未確定の通知は鳴らさない（方針は在る。無応答の通知は報告書サービスが期間キーごとに出す）。
    [Theory]
    [InlineData("scheduled")]
    [InlineData("price-movement")]
    public async Task T_10_3061_無応答の停止の印があれば定時も価格変動もLLMの前で見送る(string kind)
    {
        var llm = new CountingLlm();
        var forgone = new RecordingForgone();
        var skips = new RecordingSkips();
        var notifier = new CountingUnconfirmedNotifier();
        var service = new AppSvc(
            llm, new FixedPolicy(Policy(halted: true)), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            unconfirmedNotifier: notifier, skipReporter: skips, forgoneReporter: forgone);

        var decision = await service.DecideAsync(
            kind == "scheduled" ? Scheduled() : PriceMovement(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        llm.Calls.Should().Be(0);
        forgone.Reports.Should().ContainSingle().Which.Reason.Should().Be(DecisionForgoneBeforeLlmReason.DailyReportNoResponseHalted);
        skips.Reasons.Should().Equal(DecisionSkipReason.DailyReportNoResponseHalted);
        notifier.Calls.Should().Be(0, "「確定済みの日報が無い」通知ではない");
    }

    // T-10-3062（否定形）: 印が無ければ従来どおり判断する。
    [Fact]
    public async Task T_10_3062_印が無ければ従来どおりLLMを呼ぶ()
    {
        var llm = new CountingLlm();
        var forgone = new RecordingForgone();
        var service = new AppSvc(
            llm, new FixedPolicy(Policy(halted: false)), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            forgoneReporter: forgone);

        await service.DecideAsync(PriceMovement(), TestContext.Current.CancellationToken);

        llm.Calls.Should().BeGreaterThan(0);
        forgone.Reports.Should().BeEmpty();
    }

    private static readonly JsonSerializerOptions ReportJson =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    // 🔴 T-10-3063（契約・REST）: 送り手の本物の型 ConfirmedDailyPolicy（印つき）を送り手の JSON 設定で直列化した応答から、印と方針の両方を読む。
    // 印の無い（古い）送り手の本文は停止していないと読む。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task T_10_3063_停止の印は送り手の本物の型を直列化した応答から読める(bool halted)
    {
        var body = JsonSerializer.Serialize(
            new ReportPolicy(new DateOnly(2026, 10, 12), "前日の確定済み方針", 3, "daily-2026-10-12", 1, NoResponseHalted: halted), ReportJson);
        var provider = new HttpDailyPolicyProvider(
            new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://reports") }, NullLogger<HttpDailyPolicyProvider>.Instance);

        var policy = await provider.GetCurrentAsync(TestContext.Current.CancellationToken);

        policy!.Summary.Should().Be("前日の確定済み方針", "停止中も方針は返る");
        policy.NoResponseHalted.Should().Be(halted);

        const string legacy = """{"date":"2026-10-12","summary":"方針","assumptionsVersion":3,"periodKey":"daily-2026-10-12","version":1}""";
        var old = await new HttpDailyPolicyProvider(
            new HttpClient(new StubHandler(legacy)) { BaseAddress = new Uri("http://reports") }, NullLogger<HttpDailyPolicyProvider>.Instance)
            .GetCurrentAsync(TestContext.Current.CancellationToken);
        old!.NoResponseHalted.Should().BeFalse("印の欠落は停止していない");
    }

    // 🔴 T-10-3063（契約・gRPC）: 送り手の写し（DailyPolicyWireMapping）を受け手の写し（GrpcDailyPolicyProvider.ToPolicy）で読み戻す。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void T_10_3063_停止の印はgRPCの写しでも往復する(bool halted)
    {
        Proto.DailyPolicyRecord record = ReportWire.ToProto(
            new ReportPolicy(new DateOnly(2026, 10, 12), "方針", 3, "daily-2026-10-12", 1, NoResponseHalted: halted));

        record.HasNoResponseHalted.Should().Be(halted, "立っているときだけ載せる");
        GrpcDailyPolicyProvider.ToPolicy(record, NullLogger.Instance)!.NoResponseHalted.Should().Be(halted);
    }
}
