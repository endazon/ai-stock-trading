using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-07, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 4）, IADR-0328, IADR-0331, IADR-0446 決定 5, #1061 (#753):
// 確定済み日報の方針（`aistocktrading.report.v1.DailyPolicyRead`）を呼ぶ**本サービスの**輸送。
// 構成 `Reports:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば日報の方針のポートが `GrpcDailyPolicyProvider` を選ぶ
// （**既定は REST**。宣言しなければ本ファイルは何も登録しない＝振る舞いは 1 つも変わらない）。
// deadline の既定は REST の "reports" HttpClient.Timeout と同値（5 秒）。再試行の既定は 1 試行（規則は TradeDecisionGrpcCalls）。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない）。** 段 1 の前提条件が `GrpcChannel` を DI から引く（IADR-0427 決定 4）。
public sealed class ReportsGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://report-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "Reports:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "Reports:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "Reports:GrpcMaxAttempts";

    /// <summary>REST の "reports" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly GrpcChannel _channel;
    private readonly TradeDecisionGrpcCalls _calls;

    public ReportsGrpcTransport(GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<ReportsGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calls = new TradeDecisionGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Client = new Proto.DailyPolicyRead.DailyPolicyReadClient(channel);
    }

    internal Proto.DailyPolicyRead.DailyPolicyReadClient Client { get; }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    /// <summary>1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。</summary>
    internal Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.DailyPolicyRead.DailyPolicyReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class =>
        _calls.CallAsync(operation, fallback, options => call(Client, options), cancellationToken);

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0446 決定 5: 構成から輸送を組む登録点（段 2 の RiskManagementGrpcExtensions と同じ規則）。
public static class ReportsGrpcExtensions
{
    /// <summary><c>Reports:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。</summary>
    public static IServiceCollection AddAiStockTradingReportsGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = TradeDecisionGrpcCalls.ResolveAddress(config, ReportsGrpcTransport.AddressKey);
        if (address is null)
            return services;

        var timeout = TradeDecisionGrpcCalls.ReadTimeout(config, ReportsGrpcTransport.TimeoutKey, ReportsGrpcTransport.DefaultTimeout);
        var maxAttempts = TradeDecisionGrpcCalls.ReadMaxAttempts(config, ReportsGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp => new ReportsGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の "reports" HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<ReportsGrpcTransport>>()));
        return services;
    }
}
