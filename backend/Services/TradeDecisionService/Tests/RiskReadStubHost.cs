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

namespace TradeDecisionService.Tests;

// NFR, IADR-0427, #997 (#753): リスク管理の読み取り（`RiskControlsRead`）を**実 Kestrel の h2c 専用ポート**で立てる偽の提供側。
// 段 1 の GrpcStubHost（全体前提条件）と同じ形 —— `HttpProtocols.Http2` だけ・127.0.0.1 の空きポート。
// rpc ごとの振る舞いを差し込み、**実際に呼ばれた回数**と受け取った authorization を記録する
// （再試行の有無は回数でしか、資格情報の付与はメタデータでしか観測できない）。
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

    internal string? LastAuthorization { get; private set; }

    internal Func<int, CancellationToken, Task<Proto.GetOpenPositionsResponse>> OpenPositions { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetOpenPositionsResponse());

    internal Func<int, CancellationToken, Task<Proto.GetWorkingEntryOrdersResponse>> WorkingEntryOrders { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetWorkingEntryOrdersResponse());

    internal Func<int, CancellationToken, Task<Proto.GetSizingContextResponse>> SizingContext { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetSizingContextResponse());

    internal Task<T> Handle<T>(Func<int, CancellationToken, Task<T>> handler, ServerCallContext context)
    {
        var call = Interlocked.Increment(ref _calls);
        LastAuthorization = context.RequestHeaders.GetValue("authorization");
        return handler(call, context.CancellationToken);
    }

    internal static Func<int, CancellationToken, Task<T>> Fails<T>(StatusCode status) =>
        (call, _) => throw new RpcException(new Status(status, $"stub failure #{call}"));

    // deadline が打ち切るまで返さない（呼び出し側のキャンセルで解ける）。1 回目は暖機のため返す。
    internal static Func<int, CancellationToken, Task<T>> RespondsOnceThenHangs<T>(T first) =>
        async (call, ct) =>
        {
            if (call == 1)
                return first;
            await Task.Delay(Timeout.Infinite, ct);
            return first;
        };

    internal static Func<int, CancellationToken, Task<T>> FailsThenSucceeds<T>(StatusCode status, int failures, T ok) =>
        (call, _) => call <= failures
            ? throw new RpcException(new Status(status, $"stub failure #{call}"))
            : Task.FromResult(ok);
}

internal sealed class RiskReadStubService(RiskReadStubBehavior behavior) : Proto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<Proto.GetOpenPositionsResponse> GetOpenPositions(
        Proto.GetOpenPositionsRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.OpenPositions, context);

    public override Task<Proto.GetWorkingEntryOrdersResponse> GetWorkingEntryOrders(
        Proto.GetWorkingEntryOrdersRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.WorkingEntryOrders, context);

    public override Task<Proto.GetSizingContextResponse> GetSizingContext(
        Proto.GetSizingContextRequest request, ServerCallContext context) =>
        behavior.Handle(behavior.SizingContext, context);
}
