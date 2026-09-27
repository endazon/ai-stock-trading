using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.MarketMonitor.V1;

namespace InformationCollectionService.Infrastructure.ExternalServices;

// NFR, FR-01, FR-13, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 4）, IADR-0328, IADR-0331, IADR-0435, IADR-0446 決定 5,
// #1061 (#753):
// 監視銘柄の読み取り（`aistocktrading.marketmonitor.v1.WatchlistRead`）を呼ぶ**本サービスの**輸送。
// 構成 `MarketMonitor:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば Finnhub の対象銘柄を決める監視銘柄の読み手が
// `GrpcMarketMonitorWatchlistReader` を選ぶ（**既定は REST**）。
// deadline の既定は REST の "monitor" HttpClient.Timeout と同値（5 秒）。再試行の既定は 1 試行（規則は InformationCollectionGrpcCalls）。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。**
public sealed class MarketMonitorGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://market-monitor-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "MarketMonitor:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "MarketMonitor:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "MarketMonitor:GrpcMaxAttempts";

    /// <summary>REST の "monitor" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly GrpcChannel _channel;
    private readonly InformationCollectionGrpcCalls _calls;

    public MarketMonitorGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<MarketMonitorGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calls = new InformationCollectionGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Client = new Proto.WatchlistRead.WatchlistReadClient(channel);
    }

    internal Proto.WatchlistRead.WatchlistReadClient Client { get; }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    /// <summary>1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。</summary>
    internal Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.WatchlistRead.WatchlistReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class =>
        _calls.CallAsync(operation, fallback, options => call(Client, options), cancellationToken);

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0446 決定 5: 構成から輸送を組む登録点（段 2 の RiskManagementGrpcExtensions と同じ規則）。
public static class MarketMonitorGrpcExtensions
{
    /// <summary><c>MarketMonitor:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。</summary>
    public static IServiceCollection AddAiStockTradingMarketMonitorGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = InformationCollectionGrpcCalls.ResolveAddress(config, MarketMonitorGrpcTransport.AddressKey);
        if (address is null)
            return services;

        var timeout = InformationCollectionGrpcCalls.ReadTimeout(
            config, MarketMonitorGrpcTransport.TimeoutKey, MarketMonitorGrpcTransport.DefaultTimeout);
        var maxAttempts = InformationCollectionGrpcCalls.ReadMaxAttempts(config, MarketMonitorGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp => new MarketMonitorGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の "monitor" HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<MarketMonitorGrpcTransport>>()));
        return services;
    }
}

// NFR, IADR-0446 決定 3: 線上表現 → 本サービスの型（受け手側の写し）。
// 🔴 **原則 A**: 欠落・未指定・未知は `null`（不明）へ写す。市場は**名前で**写す（C# の 0＝日本を線上の 0＝未指定と取り違えない）。
internal static class MarketMonitorWire
{
    internal static string? Symbol(Proto.WatchlistItem item) => item.HasSymbol ? item.Symbol : null;

    internal static Market? Market(Proto.Market value) => value switch
    {
        Proto.Market.Japan => AiStockTrading.Shared.Contracts.Trading.Market.Japan,
        Proto.Market.UnitedStates => AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates,
        _ => null,
    };
}
