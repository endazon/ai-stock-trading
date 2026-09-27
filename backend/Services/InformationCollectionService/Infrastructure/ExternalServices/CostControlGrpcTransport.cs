using System.Globalization;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.CostControl.V1;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR（費用）, FR-01, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 4）, IADR-0328, IADR-0331, IADR-0031, IADR-0446 決定 5,
// #1061 (#753):
// 費用統制の判定（`aistocktrading.costcontrol.v1.CostStateRead`）を呼ぶ**本サービスの**輸送。
// 構成 `CostControl:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば統制ゲートのポートが `GrpcCostControlGate` を選ぶ
// （**既定は REST**。宣言しなければ本ファイルは何も登録しない＝振る舞いは 1 つも変わらない）。
// deadline の既定は REST の "cost" HttpClient.Timeout と同値（5 秒）。再試行の既定は 1 試行（規則は InformationCollectionGrpcCalls）。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。**
public sealed class CostControlGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://cost-control-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "CostControl:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "CostControl:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "CostControl:GrpcMaxAttempts";

    /// <summary>REST の "cost" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly GrpcChannel _channel;
    private readonly InformationCollectionGrpcCalls _calls;

    public CostControlGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<CostControlGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calls = new InformationCollectionGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Client = new Proto.CostStateRead.CostStateReadClient(channel);
    }

    internal Proto.CostStateRead.CostStateReadClient Client { get; }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    /// <summary>1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。</summary>
    internal Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.CostStateRead.CostStateReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class =>
        _calls.CallAsync(operation, fallback, options => call(Client, options), cancellationToken);

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0446 決定 5: 構成から輸送を組む登録点（段 2 の RiskManagementGrpcExtensions と同じ規則）。
public static class CostControlGrpcExtensions
{
    /// <summary><c>CostControl:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。</summary>
    public static IServiceCollection AddAiStockTradingCostControlGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = InformationCollectionGrpcCalls.ResolveAddress(config, CostControlGrpcTransport.AddressKey);
        if (address is null)
            return services;

        var timeout = InformationCollectionGrpcCalls.ReadTimeout(
            config, CostControlGrpcTransport.TimeoutKey, CostControlGrpcTransport.DefaultTimeout);
        var maxAttempts = InformationCollectionGrpcCalls.ReadMaxAttempts(config, CostControlGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp => new CostControlGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の "cost" HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<CostControlGrpcTransport>>()));
        return services;
    }
}

// NFR, IADR-0446 決定 3: 線上表現 → 本サービスの型（受け手側の写し）。
// 🔴 **原則 A**: 欠落・空は `null`（言っていない）。0・false へ写さない。読めない 10 進は FormatException（呼び出し元が不正応答へ倒す）。
internal static class CostControlWire
{
    internal static bool? IsHalted(Proto.GetCostStateResponse response) => response.HasIsHalted ? response.IsHalted : null;

    internal static decimal? IntervalMultiplier(Proto.GetCostStateResponse response) =>
        response.HasIntervalMultiplier && !string.IsNullOrEmpty(response.IntervalMultiplier)
            ? decimal.Parse(response.IntervalMultiplier, NumberStyles.Number, CultureInfo.InvariantCulture)
            : null;
}
