extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;
using RiskReadWireMapping = RiskManagementWorker::RiskManagementService.Features.RiskManagement.RiskReadWireMapping;
using TradingHaltView = RiskManagementWorker::RiskManagementService.Features.RiskManagement.GetTradingHalt.TradingHaltView;

namespace OrderExecutionService.Tests;

// 🔴 T-10-3083, NFR, FR-10, ADR-0009, MSP:ADR-0029, IADR-0284 決定 5（段 5 の追補 3）, IADR-0567 決定 2・4, IADR-0604, #1510 (#753):
// 発注執行が gRPC（`RiskControlsRead/GetTradingHalt`）で読む停止の状態の**契約**と**失敗の意味**を、実 Kestrel の h2c の偽の提供側で固定する。
// 🔴 解釈は REST（HttpTradingHaltSource.Interpret）と同じ 1 つ。送り手の本物の型を、REST へは web 既定で、gRPC へは送り手の写し
// （RiskReadWireMapping）で出し、**両方の受け手が同じ答えを返す**ことを見る。欠落・失敗・時間切れ・取り消しはどれも null（不明＝新規建てを送らない）。
public class GrpcTradingHaltSourceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static ServiceProvider Compose(string address, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["RiskManagement:Grpc"] = address };
        foreach (var (k, v) in extra ?? [])
            values[k] = v;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingRiskManagementGrpc(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    private static async Task<(TradingHaltState? State, TradingHaltStubBehavior Behavior)> FetchAsync(
        TradingHaltStubBehavior behavior, Dictionary<string, string?>? extra = null, CancellationToken? cancellationToken = null)
    {
        await using var host = await TradingHaltGrpcStubHost.StartAsync(behavior);
        await using var sp = Compose(host.Address, extra);
        var source = new GrpcTradingHaltSource(
            sp.GetRequiredService<RiskManagementGrpcTransport>(), NullLogger<GrpcTradingHaltSource>.Instance);
        return (await source.GetAsync(cancellationToken ?? TestContext.Current.CancellationToken), behavior);
    }

    private sealed class JsonStub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task T_10_3083_送り手の型の停止の状態を_REST_と同じ値で読む(bool killSwitch, bool paused)
    {
        var view = new TradingHaltView(killSwitch, paused);

        var (grpc, behavior) = await FetchAsync(TradingHaltStubBehavior.Returns(RiskReadWireMapping.ToProto(view)));
        var rest = await new HttpTradingHaltSource(
                new HttpClient(new JsonStub(JsonSerializer.Serialize(view, Web))) { BaseAddress = new Uri("http://risk") },
                NullLogger<HttpTradingHaltSource>.Instance)
            .GetAsync(TestContext.Current.CancellationToken);

        grpc.Should().Be(new TradingHaltState(killSwitch, paused));
        grpc.Should().Be(rest, "解釈は REST と同じ 1 つ");
        behavior.Calls.Should().Be(1);
    }

    // 🔴 欠落は「停止していない」ではなく不明（proto3 の暗黙の false で読まない）。
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task T_10_3083_項目の欠落は不明(bool hasKillSwitch, bool hasPaused)
    {
        var response = new Proto.GetTradingHaltResponse();
        if (hasKillSwitch)
            response.KillSwitchEngaged = false;
        if (hasPaused)
            response.TradingPaused = false;

        var (state, _) = await FetchAsync(TradingHaltStubBehavior.Returns(response));

        state.Should().BeNull();
    }

    [Theory]
    [InlineData(StatusCode.Unavailable)]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    [InlineData(StatusCode.Internal)]
    public async Task T_10_3083_失敗はどれも不明で既定は再試行しない(StatusCode status)
    {
        var (state, behavior) = await FetchAsync(TradingHaltStubBehavior.Fails(status));

        state.Should().BeNull();
        behavior.Calls.Should().Be(1);
    }

    [Fact]
    public async Task T_10_3083_時間切れは不明で既定の_deadline_は_REST_と同じ()
    {
        RiskManagementGrpcTransport.DefaultTimeout.Should().Be(HttpTradingHaltSource.RequestTimeout);

        var (state, _) = await FetchAsync(
            TradingHaltStubBehavior.Hangs(), new() { ["RiskManagement:GrpcTimeoutSeconds"] = "1" });

        state.Should().BeNull();
    }

    // 🔴 呼び出し元の取り消しでも投げずに不明（REST と同じ。予約を取った後に呼ばれるため。IADR-0567 決定 4）。
    [Fact]
    public async Task T_10_3083_呼び出し元の取り消しでも投げずに不明()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var (state, _) = await FetchAsync(
            TradingHaltStubBehavior.Returns(new Proto.GetTradingHaltResponse { KillSwitchEngaged = false, TradingPaused = false }),
            cancellationToken: cts.Token);

        state.Should().BeNull();
    }
}
