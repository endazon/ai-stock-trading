extern alias CostControlWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;
using CostControlDecision = CostControlWorker::CostControlService.Domain.CostControlDecision;
using CostControlState = CostControlWorker::CostControlService.Domain.CostControlState;
using CostStateWireMapping = CostControlWorker::CostControlService.Features.CostControl.CostStateWireMapping;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace TradeDecisionService.Tests;

// 🔴 T-10-3085, NFR-13, ADR-0074 決定2, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0582, IADR-0604, #1510 (#753):
// 取引判断が gRPC（`CostStateRead/GetCostState`）で読む月次 LLM 費用の停止の**契約**と**失敗の意味**を、実 Kestrel の h2c の偽の提供側で固定する。
// 送り手の本物の型を、REST へは送り手の JSON 設定で、gRPC へは送り手の写し（CostStateWireMapping）で出し、**両方の受け手が同じ答え**を返すことを見る。
// 停止は `is_halted` が明示的に true のときだけ。欠落・失敗・時間切れは「停止していない」（REST と同じ向き）。
public class GrpcLlmCostHaltGateTests
{
    private static readonly JsonSerializerOptions CostWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<CostControlState, decimal, bool> Decisions => new()
    {
        { CostControlState.Normal, 1m, false },
        { CostControlState.Throttled, 2m, false },
        { CostControlState.Halted, 0m, true },
    };

    private static async Task<(bool Halted, CostStateStubBehavior Behavior)> AskAsync(
        CostStateStubBehavior behavior, Dictionary<string, string?>? extra = null)
    {
        await using var host = await CostStateGrpcStubHost.StartAsync(behavior);
        var values = new Dictionary<string, string?> { ["CostControl:Grpc"] = host.Address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingCostControlGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        await using var sp = services.BuildServiceProvider();
        var gate = new GrpcLlmCostHaltGate(sp.GetRequiredService<CostControlGrpcTransport>(), NullLogger<GrpcLlmCostHaltGate>.Instance);
        return (await gate.IsHaltedAsync(TestContext.Current.CancellationToken), behavior);
    }

    [Theory]
    [MemberData(nameof(Decisions))]
    public async Task T_10_3085_送り手の型の停止を_REST_と同じ値で読む(CostControlState state, decimal multiplier, bool halted)
    {
        var decision = new CostControlDecision(state, multiplier);

        var (grpc, behavior) = await AskAsync(CostStateStubBehavior.Returns(CostStateWireMapping.ToProto(decision)));
        var rest = await new HttpLlmCostHaltGate(
                new HttpClient(new JsonStub(JsonSerializer.Serialize(decision, CostWire))) { BaseAddress = new Uri("http://cost-control") },
                NullLogger<HttpLlmCostHaltGate>.Instance)
            .IsHaltedAsync(TestContext.Current.CancellationToken);

        grpc.Should().Be(halted);
        grpc.Should().Be(rest, "倒す向きは REST と同じ");
        behavior.Calls.Should().Be(1);
    }

    [Fact]
    public async Task T_10_3085_停止の欠落は停止していない()
    {
        var (halted, _) = await AskAsync(CostStateStubBehavior.Returns(new Proto.GetCostStateResponse { IntervalMultiplier = "0" }));

        halted.Should().BeFalse();
        GrpcLlmCostHaltGate.Interpret(null, NullLogger.Instance).Should().BeFalse("応答なしも停止していない");
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Internal)]
    public async Task T_10_3085_失敗は停止していないで既定は再試行しない(StatusCode status)
    {
        var (halted, behavior) = await AskAsync(CostStateStubBehavior.Fails(status));

        halted.Should().BeFalse();
        behavior.Calls.Should().Be(1);
    }

    [Fact]
    public async Task T_10_3085_時間切れは停止していないで既定の_deadline_は_REST_と同じ_3_秒()
    {
        CostControlGrpcTransport.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(3));

        var (halted, _) = await AskAsync(CostStateStubBehavior.Hangs(), new() { ["CostControl:GrpcTimeoutSeconds"] = "1" });

        halted.Should().BeFalse();
    }

    private sealed class JsonStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
