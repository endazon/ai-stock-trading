using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace RiskManagementService.Tests;

// NFR, IADR-0559, #1391 (#753): 発注執行の読み取り（`OrderExecutionRead`）を**実 Kestrel の h2c 専用ポート**（127.0.0.1 のみ）で立てる
// 偽の提供側（段 4 の Stage4ReadStubHost と同じ形）。rpc ごとに呼ばれた回数と最後の要求を記録する。
// 🔴 サービスを跨いだ共通化はしない（呼び出し元ごとの複製。IADR-0427 決定 4 と同じ理由でテストの足場も各テストプロジェクトに置く）。
internal sealed class OrderExecutionReadStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private OrderExecutionReadStubHost(WebApplication app, OrderExecutionReadStubBehavior behavior, string address)
    {
        _app = app;
        Behavior = behavior;
        Address = address;
    }

    internal OrderExecutionReadStubBehavior Behavior { get; }

    internal string Address { get; }

    internal static async Task<OrderExecutionReadStubHost> StartAsync(OrderExecutionReadStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<OrderExecutionReadStubService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new OrderExecutionReadStubHost(app, behavior, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class OrderExecutionReadStubBehavior
{
    private int _shortPermitCalls;
    private int _dailyBarsCalls;
    private int _inFlightClosesCalls;

    internal int ShortPermitCalls => Volatile.Read(ref _shortPermitCalls);

    internal int DailyBarsCalls => Volatile.Read(ref _dailyBarsCalls);

    internal int InFlightClosesCalls => Volatile.Read(ref _inFlightClosesCalls);

    internal Proto.GetShortPermitRequest? LastShortPermit { get; private set; }

    internal Proto.GetDailyBarsRequest? LastDailyBars { get; private set; }

    internal Proto.GetInFlightClosesRequest? LastInFlightCloses { get; private set; }

    internal Func<int, CancellationToken, Task<Proto.GetShortPermitResponse>> ShortPermit { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetShortPermitResponse());

    internal Func<int, CancellationToken, Task<Proto.GetDailyBarsResponse>> DailyBars { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetDailyBarsResponse());

    internal Func<int, CancellationToken, Task<Proto.GetInFlightClosesResponse>> InFlightCloses { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetInFlightClosesResponse());

    internal Task<Proto.GetShortPermitResponse> HandleShortPermit(Proto.GetShortPermitRequest request, ServerCallContext context)
    {
        LastShortPermit = request;
        return ShortPermit(Interlocked.Increment(ref _shortPermitCalls), context.CancellationToken);
    }

    internal Task<Proto.GetDailyBarsResponse> HandleDailyBars(Proto.GetDailyBarsRequest request, ServerCallContext context)
    {
        LastDailyBars = request;
        return DailyBars(Interlocked.Increment(ref _dailyBarsCalls), context.CancellationToken);
    }

    internal Task<Proto.GetInFlightClosesResponse> HandleInFlightCloses(Proto.GetInFlightClosesRequest request, ServerCallContext context)
    {
        LastInFlightCloses = request;
        return InFlightCloses(Interlocked.Increment(ref _inFlightClosesCalls), context.CancellationToken);
    }

    internal static Func<int, CancellationToken, Task<T>> Returns<T>(T response) => (_, _) => Task.FromResult(response);

    internal static Func<int, CancellationToken, Task<T>> Fails<T>(StatusCode status) =>
        (call, _) => throw new RpcException(new Status(status, $"stub failure #{call}"));

    internal static Func<int, CancellationToken, Task<T>> FailsThenSucceeds<T>(StatusCode status, int failures, T ok) =>
        (call, _) => call <= failures
            ? throw new RpcException(new Status(status, $"stub failure #{call}"))
            : Task.FromResult(ok);
}

internal sealed class OrderExecutionReadStubService(OrderExecutionReadStubBehavior behavior)
    : Proto.OrderExecutionRead.OrderExecutionReadBase
{
    public override Task<Proto.GetShortPermitResponse> GetShortPermit(
        Proto.GetShortPermitRequest request, ServerCallContext context) =>
        behavior.HandleShortPermit(request, context);

    public override Task<Proto.GetDailyBarsResponse> GetDailyBars(
        Proto.GetDailyBarsRequest request, ServerCallContext context) =>
        behavior.HandleDailyBars(request, context);

    public override Task<Proto.GetInFlightClosesResponse> GetInFlightCloses(
        Proto.GetInFlightClosesRequest request, ServerCallContext context) =>
        behavior.HandleInFlightCloses(request, context);
}
