using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace TradeDecisionService.Tests;

// NFR, IADR-0427, IADR-0604, #1510 (#753): 費用統制の判定（`CostStateRead/GetCostState`）を**実 Kestrel の h2c 専用ポート**
// （127.0.0.1 のみ）で立てる偽の提供側（情報収集の Stage4ReadStubHost と同じ形。テストプロジェクトごとに置く）。呼ばれた回数を数える。
internal sealed class CostStateGrpcStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private CostStateGrpcStubHost(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    internal string Address { get; }

    internal static async Task<CostStateGrpcStubHost> StartAsync(CostStateStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<CostStateStubService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new CostStateGrpcStubHost(app, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class CostStateStubBehavior(Func<int, CancellationToken, Task<Proto.GetCostStateResponse>> respond)
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal Task<Proto.GetCostStateResponse> Handle(ServerCallContext context)
    {
        var call = Interlocked.Increment(ref _calls);
        return respond(call, context.CancellationToken);
    }

    internal static CostStateStubBehavior Returns(Proto.GetCostStateResponse response) =>
        new((_, _) => Task.FromResult(response));

    internal static CostStateStubBehavior Fails(StatusCode status) =>
        new((call, _) => throw new RpcException(new Status(status, $"stub failure #{call}")));

    internal static CostStateStubBehavior Hangs() =>
        new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Proto.GetCostStateResponse();
        });
}

internal sealed class CostStateStubService(CostStateStubBehavior behavior) : Proto.CostStateRead.CostStateReadBase
{
    public override Task<Proto.GetCostStateResponse> GetCostState(
        Proto.GetCostStateRequest request, ServerCallContext context) =>
        behavior.Handle(context);
}
