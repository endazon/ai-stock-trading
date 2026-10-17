using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CostProto = AiStockTrading.Shared.Grpc.CostControl.V1;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using RiskProto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace InformationCollectionService.Tests;

// NFR, IADR-0446, #1061 (#753): 段 4 で情報収集が読む 2 つの提供側（市場監視の `WatchlistRead`・費用統制の `CostStateRead`）を
// **実 Kestrel の h2c 専用ポート**（127.0.0.1 のみ）で立てる偽の提供側（取引判断の同名の型と同じ形。テストプロジェクトごとに置く）。
// #1388, IADR-0557: 段 5 の追補でリスク管理の保有（`RiskControlsRead/GetOpenPositions`）も同じ偽の提供側に載せた。
internal sealed class Stage4ReadStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private Stage4ReadStubHost(WebApplication app, Stage4ReadStubBehavior behavior, string address)
    {
        _app = app;
        Behavior = behavior;
        Address = address;
    }

    internal Stage4ReadStubBehavior Behavior { get; }

    internal string Address { get; }

    internal static async Task<Stage4ReadStubHost> StartAsync(Stage4ReadStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<WatchlistStubService>();
        app.MapGrpcService<CostStateStubService>();
        app.MapGrpcService<RiskOpenPositionsStubService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Stage4ReadStubHost(app, behavior, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class Stage4ReadStubBehavior
{
    private int _watchlistCalls;
    private int _costCalls;
    private int _openPositionsCalls;

    internal int WatchlistCalls => Volatile.Read(ref _watchlistCalls);

    internal int CostCalls => Volatile.Read(ref _costCalls);

    internal int OpenPositionsCalls => Volatile.Read(ref _openPositionsCalls);

    internal Func<int, CancellationToken, Task<MonitorProto.GetWatchlistResponse>> Watchlist { get; init; } =
        (_, _) => Task.FromResult(new MonitorProto.GetWatchlistResponse());

    internal Func<int, CancellationToken, Task<CostProto.GetCostStateResponse>> Cost { get; init; } =
        (_, _) => Task.FromResult(new CostProto.GetCostStateResponse { IsHalted = false, IntervalMultiplier = "1" });

    internal Func<int, CancellationToken, Task<RiskProto.GetOpenPositionsResponse>> OpenPositions { get; init; } =
        (_, _) => Task.FromResult(new RiskProto.GetOpenPositionsResponse());

    internal Task<MonitorProto.GetWatchlistResponse> HandleWatchlist(ServerCallContext context) =>
        Watchlist(Interlocked.Increment(ref _watchlistCalls), context.CancellationToken);

    internal Task<CostProto.GetCostStateResponse> HandleCost(ServerCallContext context) =>
        Cost(Interlocked.Increment(ref _costCalls), context.CancellationToken);

    internal Task<RiskProto.GetOpenPositionsResponse> HandleOpenPositions(ServerCallContext context) =>
        OpenPositions(Interlocked.Increment(ref _openPositionsCalls), context.CancellationToken);

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

internal sealed class WatchlistStubService(Stage4ReadStubBehavior behavior) : MonitorProto.WatchlistRead.WatchlistReadBase
{
    public override Task<MonitorProto.GetWatchlistResponse> GetWatchlist(
        MonitorProto.GetWatchlistRequest request, ServerCallContext context) =>
        behavior.HandleWatchlist(context);
}

internal sealed class CostStateStubService(Stage4ReadStubBehavior behavior) : CostProto.CostStateRead.CostStateReadBase
{
    public override Task<CostProto.GetCostStateResponse> GetCostState(
        CostProto.GetCostStateRequest request, ServerCallContext context) =>
        behavior.HandleCost(context);
}

internal sealed class RiskOpenPositionsStubService(Stage4ReadStubBehavior behavior) : RiskProto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<RiskProto.GetOpenPositionsResponse> GetOpenPositions(
        RiskProto.GetOpenPositionsRequest request, ServerCallContext context) =>
        behavior.HandleOpenPositions(context);
}
