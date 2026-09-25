using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Tests;

// NFR, IADR-0427, #997 (#753): リスク管理の読み取り（`RiskControlsRead`）を**実 Kestrel の h2c 専用ポート**で立てる偽の提供側
// （取引判断・市場監視の同名の型と同じ形。テストプロジェクトごとに置く）。呼ばれた回数と最後の要求（期間）を記録する。
internal sealed class RiskReadStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RiskReadStubHost(WebApplication app, RiskReadStubBehavior behavior, string address)
    {
        _app = app;
        Behavior = behavior;
        Address = address;
    }

    internal RiskReadStubBehavior Behavior { get; }

    internal string Address { get; }

    internal static async Task<RiskReadStubHost> StartAsync(RiskReadStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<RiskReadStubService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RiskReadStubHost(app, behavior, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class RiskReadStubBehavior
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    /// <summary>最後に受け取った期間（from, to）。期間を取る rpc のときだけ入る。</summary>
    internal (string From, string To)? LastPeriod { get; private set; }

    internal Func<int, CancellationToken, Task<Proto.GetOpenPositionsResponse>> OpenPositions { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetOpenPositionsResponse());

    internal Func<int, CancellationToken, Task<Proto.GetStageGateResponse>> StageGate { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetStageGateResponse());

    internal Func<int, CancellationToken, Task<Proto.GetFillsResponse>> Fills { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetFillsResponse());

    internal Func<int, CancellationToken, Task<Proto.GetDriftAdoptionsResponse>> DriftAdoptions { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetDriftAdoptionsResponse());

    internal Func<int, CancellationToken, Task<Proto.GetBuyInInferencesResponse>> BuyInInferences { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetBuyInInferencesResponse());

    internal Func<int, CancellationToken, Task<Proto.GetSessionUptimeResponse>> SessionUptime { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetSessionUptimeResponse());

    internal Task<T> Handle<T>(Func<int, CancellationToken, Task<T>> handler, ServerCallContext context, string? from = null, string? to = null)
    {
        var call = Interlocked.Increment(ref _calls);
        if (from is not null && to is not null)
            LastPeriod = (from, to);
        return handler(call, context.CancellationToken);
    }

    internal static Func<int, CancellationToken, Task<T>> Returns<T>(T response) => (_, _) => Task.FromResult(response);

    internal static Func<int, CancellationToken, Task<T>> Fails<T>(StatusCode status) =>
        (call, _) => throw new RpcException(new Status(status, $"stub failure #{call}"));

    internal static Func<int, CancellationToken, Task<T>> FailsThenSucceeds<T>(StatusCode status, int failures, T ok) =>
        (call, _) => call <= failures
            ? throw new RpcException(new Status(status, $"stub failure #{call}"))
            : Task.FromResult(ok);

    internal static Func<int, CancellationToken, Task<T>> RespondsOnceThenHangs<T>(T first) =>
        async (call, ct) =>
        {
            if (call > 1)
                await Task.Delay(Timeout.Infinite, ct);
            return first;
        };
}

internal sealed class RiskReadStubService(RiskReadStubBehavior behavior) : Proto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<Proto.GetOpenPositionsResponse> GetOpenPositions(
        Proto.GetOpenPositionsRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.OpenPositions, context);

    public override Task<Proto.GetStageGateResponse> GetStageGate(
        Proto.GetStageGateRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.StageGate, context);

    public override Task<Proto.GetFillsResponse> GetFills(Proto.GetFillsRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.Fills, context, request.From, request.To);

    public override Task<Proto.GetDriftAdoptionsResponse> GetDriftAdoptions(
        Proto.GetDriftAdoptionsRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.DriftAdoptions, context, request.From, request.To);

    public override Task<Proto.GetBuyInInferencesResponse> GetBuyInInferences(
        Proto.GetBuyInInferencesRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.BuyInInferences, context, request.From, request.To);

    public override Task<Proto.GetSessionUptimeResponse> GetSessionUptime(
        Proto.GetSessionUptimeRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.SessionUptime, context, request.From, request.To);
}
