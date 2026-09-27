using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitorProto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;
using ReportProto = AiStockTrading.Shared.Grpc.Report.V1;

namespace TradeDecisionService.Tests;

// NFR, IADR-0446, #1061 (#753): 段 4 で取引判断が読む 2 つの提供側（報告書の `DailyPolicyRead`・市場監視の `WatchlistRead`）を
// **実 Kestrel の h2c 専用ポート**（127.0.0.1 のみ）で立てる偽の提供側（段 2 の RiskReadStubHost と同じ形）。
// rpc ごとに呼ばれた回数と、当時の監視銘柄の要求の時刻を記録する。
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
        app.MapGrpcService<DailyPolicyStubService>();
        app.MapGrpcService<WatchlistStubService>();
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
    private int _policyCalls;
    private int _watchlistCalls;
    private int _asOfCalls;

    internal int PolicyCalls => Volatile.Read(ref _policyCalls);

    internal int WatchlistCalls => Volatile.Read(ref _watchlistCalls);

    internal int AsOfCalls => Volatile.Read(ref _asOfCalls);

    internal string? LastAsOfAt { get; private set; }

    internal Func<int, CancellationToken, Task<ReportProto.GetConfirmedDailyPolicyResponse>> Policy { get; init; } =
        (_, _) => Task.FromResult(new ReportProto.GetConfirmedDailyPolicyResponse());

    internal Func<int, CancellationToken, Task<MonitorProto.GetWatchlistResponse>> Watchlist { get; init; } =
        (_, _) => Task.FromResult(new MonitorProto.GetWatchlistResponse());

    internal Func<int, CancellationToken, Task<MonitorProto.GetWatchlistAsOfResponse>> AsOf { get; init; } =
        (_, _) => Task.FromResult(new MonitorProto.GetWatchlistAsOfResponse());

    internal Task<ReportProto.GetConfirmedDailyPolicyResponse> HandlePolicy(ServerCallContext context) =>
        Policy(Interlocked.Increment(ref _policyCalls), context.CancellationToken);

    internal Task<MonitorProto.GetWatchlistResponse> HandleWatchlist(ServerCallContext context) =>
        Watchlist(Interlocked.Increment(ref _watchlistCalls), context.CancellationToken);

    internal Task<MonitorProto.GetWatchlistAsOfResponse> HandleAsOf(string at, ServerCallContext context)
    {
        LastAsOfAt = at;
        return AsOf(Interlocked.Increment(ref _asOfCalls), context.CancellationToken);
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

internal sealed class DailyPolicyStubService(Stage4ReadStubBehavior behavior) : ReportProto.DailyPolicyRead.DailyPolicyReadBase
{
    public override Task<ReportProto.GetConfirmedDailyPolicyResponse> GetConfirmedDailyPolicy(
        ReportProto.GetConfirmedDailyPolicyRequest request, ServerCallContext context) =>
        behavior.HandlePolicy(context);
}

internal sealed class WatchlistStubService(Stage4ReadStubBehavior behavior) : MonitorProto.WatchlistRead.WatchlistReadBase
{
    public override Task<MonitorProto.GetWatchlistResponse> GetWatchlist(
        MonitorProto.GetWatchlistRequest request, ServerCallContext context) =>
        behavior.HandleWatchlist(context);

    public override Task<MonitorProto.GetWatchlistAsOfResponse> GetWatchlistAsOf(
        MonitorProto.GetWatchlistAsOfRequest request, ServerCallContext context) =>
        behavior.HandleAsOf(request.At, context);
}
