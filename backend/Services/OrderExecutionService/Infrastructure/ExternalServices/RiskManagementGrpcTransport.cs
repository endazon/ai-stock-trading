using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// NFR, FR-10, ADR-0009, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 5 の追補 3）, IADR-0427 決定 4・5, IADR-0567, IADR-0604, #1510 (#753):
// リスク管理の読み取り（`aistocktrading.riskmanagement.v1.RiskControlsRead`）を呼ぶ**本サービスの**輸送。
// 構成 `RiskManagement:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば停止状態の照会口（新規建てを送る直前の最終照会）が
// gRPC 実装を選ぶ（**既定は REST**。宣言が無ければ本ファイルは何も登録しない）。
// deadline の既定は REST の "risk" HttpClient.Timeout と同値（3 秒＝HttpTradingHaltSource.RequestTimeout）。再試行の既定は 1 試行。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。**
public sealed class RiskManagementGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://risk-management-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "RiskManagement:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "RiskManagement:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "RiskManagement:GrpcMaxAttempts";

    /// <summary>REST の "risk" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = HttpTradingHaltSource.RequestTimeout;

    private readonly GrpcChannel _channel;
    private readonly OrderExecutionGrpcCalls _calls;

    public RiskManagementGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<RiskManagementGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calls = new OrderExecutionGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Client = new Proto.RiskControlsRead.RiskControlsReadClient(channel);
    }

    internal Proto.RiskControlsRead.RiskControlsReadClient Client { get; }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    /// <summary>1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。</summary>
    internal Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.RiskControlsRead.RiskControlsReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class =>
        _calls.CallAsync(operation, fallback, options => call(Client, options), cancellationToken);

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0604: 構成から輸送を組む登録点（他のサービスの RiskManagementGrpcExtensions と同じ規則）。
public static class RiskManagementGrpcExtensions
{
    /// <summary><c>RiskManagement:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。</summary>
    public static IServiceCollection AddAiStockTradingRiskManagementGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = OrderExecutionGrpcCalls.ResolveAddress(config, RiskManagementGrpcTransport.AddressKey);
        if (address is null)
            return services;

        var timeout = OrderExecutionGrpcCalls.ReadTimeout(
            config, RiskManagementGrpcTransport.TimeoutKey, RiskManagementGrpcTransport.DefaultTimeout);
        var maxAttempts = OrderExecutionGrpcCalls.ReadMaxAttempts(config, RiskManagementGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp => new RiskManagementGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の "risk" HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<RiskManagementGrpcTransport>>()));
        return services;
    }
}
