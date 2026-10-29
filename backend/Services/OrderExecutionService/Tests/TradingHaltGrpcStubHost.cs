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

namespace OrderExecutionService.Tests;

// NFR, IADR-0427, IADR-0604, #1510 (#753): リスク管理の停止の状態（`RiskControlsRead/GetTradingHalt`）を**実 Kestrel の h2c 専用ポート**
// （127.0.0.1 のみ）で立てる偽の提供側（他サービスの RiskReadStubHost と同じ形。テストプロジェクトごとに置く）。呼ばれた回数を数える。
internal sealed class TradingHaltGrpcStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TradingHaltGrpcStubHost(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    internal string Address { get; }

    internal static async Task<TradingHaltGrpcStubHost> StartAsync(TradingHaltStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<TradingHaltStubService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TradingHaltGrpcStubHost(app, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class TradingHaltStubBehavior(Func<int, CancellationToken, Task<Proto.GetTradingHaltResponse>> respond)
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal Task<Proto.GetTradingHaltResponse> Handle(ServerCallContext context)
    {
        var call = Interlocked.Increment(ref _calls);
        return respond(call, context.CancellationToken);
    }

    internal static TradingHaltStubBehavior Returns(Proto.GetTradingHaltResponse response) =>
        new((_, _) => Task.FromResult(response));

    internal static TradingHaltStubBehavior Fails(StatusCode status) =>
        new((call, _) => throw new RpcException(new Status(status, $"stub failure #{call}")));

    internal static TradingHaltStubBehavior Hangs() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Proto.GetTradingHaltResponse();
        });
}

internal sealed class TradingHaltStubService(TradingHaltStubBehavior behavior) : Proto.RiskControlsRead.RiskControlsReadBase
{
    public override Task<Proto.GetTradingHaltResponse> GetTradingHalt(
        Proto.GetTradingHaltRequest request, ServerCallContext context) =>
        behavior.Handle(context);
}
