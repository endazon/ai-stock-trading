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

namespace MarketMonitorService.Tests;

// NFR, IADR-0427, #997 (#753): リスク管理の読み取り（`RiskControlsRead/GetOpenPositions`）を**実 Kestrel の h2c 専用ポート**で
// 立てる偽の提供側（取引判断の同名の型と同じ形。テストプロジェクトごとに置く）。呼ばれた回数を数える。
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

internal sealed class RiskReadStubBehavior(Func<int, CancellationToken, Task<Proto.GetOpenPositionsResponse>> openPositions)
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal Task<Proto.GetOpenPositionsResponse> Handle(ServerCallContext context)
    {
        var call = Interlocked.Increment(ref _calls);
        return openPositions(call, context.CancellationToken);
    }

    internal static RiskReadStubBehavior Returns(params Proto.OpenPositionRow[] rows) =>
        new((_, _) =>
        {
            var r = new Proto.GetOpenPositionsResponse();
            r.Positions.AddRange(rows);
            return Task.FromResult(r);
        });

    internal static RiskReadStubBehavior Fails(StatusCode status) =>
        new((call, _) => throw new RpcException(new Status(status, $"stub failure #{call}")));

    internal static RiskReadStubBehavior FailsThenSucceeds(StatusCode status, int failures, params Proto.OpenPositionRow[] rows) =>
        new((call, _) =>
        {
            if (call <= failures)
                throw new RpcException(new Status(status, $"stub failure #{call}"));
            var r = new Proto.GetOpenPositionsResponse();
            r.Positions.AddRange(rows);
            return Task.FromResult(r);
        });

    // 1 回目は暖機のため応答し、2 回目以降は deadline が打ち切るまで返さない。
    internal static RiskReadStubBehavior RespondsOnceThenHangs(params Proto.OpenPositionRow[] rows) =>
        new(async (call, ct) =>
        {
            if (call > 1)
                await Task.Delay(Timeout.Infinite, ct);
            var r = new Proto.GetOpenPositionsResponse();
            r.Positions.AddRange(rows);
            return r;
        });
}

internal sealed class RiskReadStubService(RiskReadStubBehavior behavior) : Proto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<Proto.GetOpenPositionsResponse> GetOpenPositions(
        Proto.GetOpenPositionsRequest request, ServerCallContext context) =>
        behavior.Handle(context);
}
