using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.Audit.V1;

namespace ReportService.Tests;

// NFR, IADR-0445, #1059 (#753): 監査台帳の読み取り（`AuditEventsRead`）を**実 Kestrel の h2c 専用ポート**（127.0.0.1 のみ）で立てる
// 偽の提供側（段 2 の RiskReadStubHost と同じ形）。呼ばれた回数と最後の要求（期間・種別）を記録する。
internal sealed class AuditReadStubHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AuditReadStubHost(WebApplication app, AuditReadStubBehavior behavior, string address)
    {
        _app = app;
        Behavior = behavior;
        Address = address;
    }

    internal AuditReadStubBehavior Behavior { get; }

    internal string Address { get; }

    internal static async Task<AuditReadStubHost> StartAsync(AuditReadStubBehavior behavior)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(behavior);

        var app = builder.Build();
        app.MapGrpcService<AuditReadStubService>();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new AuditReadStubHost(app, behavior, address);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class AuditReadStubBehavior
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    /// <summary>最後に受け取った要求（期間と種別）。</summary>
    internal Proto.GetEventsByTypeRequest? LastRequest { get; private set; }

    internal Func<int, CancellationToken, Task<Proto.GetEventsByTypeResponse>> EventsByType { get; init; } =
        (_, _) => Task.FromResult(new Proto.GetEventsByTypeResponse());

    internal Task<Proto.GetEventsByTypeResponse> Handle(Proto.GetEventsByTypeRequest request, ServerCallContext context)
    {
        var call = Interlocked.Increment(ref _calls);
        LastRequest = request;
        return EventsByType(call, context.CancellationToken);
    }

    internal static Func<int, CancellationToken, Task<Proto.GetEventsByTypeResponse>> Returns(params Proto.LedgerRecord[] records)
    {
        var response = new Proto.GetEventsByTypeResponse();
        response.Records.AddRange(records);
        return (_, _) => Task.FromResult(response);
    }

    internal static Func<int, CancellationToken, Task<Proto.GetEventsByTypeResponse>> Fails(StatusCode status) =>
        (call, _) => throw new RpcException(new Status(status, $"stub failure #{call}"));

    internal static Func<int, CancellationToken, Task<Proto.GetEventsByTypeResponse>> FailsThenReturns(
        StatusCode status, int failures, params Proto.LedgerRecord[] records)
    {
        var ok = Returns(records);
        return (call, ct) => call <= failures
            ? throw new RpcException(new Status(status, $"stub failure #{call}"))
            : ok(call, ct);
    }

    internal static Func<int, CancellationToken, Task<Proto.GetEventsByTypeResponse>> RespondsOnceThenHangs(
        params Proto.LedgerRecord[] records)
    {
        var ok = Returns(records);
        return async (call, ct) =>
        {
            if (call > 1)
                await Task.Delay(Timeout.Infinite, ct);
            return await ok(call, ct);
        };
    }
}

internal sealed class AuditReadStubService(AuditReadStubBehavior behavior) : Proto.AuditEventsRead.AuditEventsReadBase
{
    public override Task<Proto.GetEventsByTypeResponse> GetEventsByType(
        Proto.GetEventsByTypeRequest request, ServerCallContext context) =>
        behavior.Handle(request, context);
}
