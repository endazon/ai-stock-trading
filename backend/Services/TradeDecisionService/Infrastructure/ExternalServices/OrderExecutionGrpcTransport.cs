using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.OrderExecution.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-10, FR-15, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 5 の追補 2）, IADR-0427 決定 4・5, IADR-0559 決定 3, #1391 (#753):
// 発注執行の読み取り（`aistocktrading.orderexecution.v1.OrderExecutionRead`）を呼ぶ**本サービスの**輸送。
// 構成 `OrderExecution:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば日足の口（出来高・ATR(14)・Stage 0 の当時の価格）と
// 処理中の決済の照会口が gRPC 実装を選ぶ（**既定は REST**。宣言が無ければ本ファイルは何も登録しない）。
// deadline の既定は REST の "order-execution" HttpClient.Timeout と同値（8 秒＝HttpDailyBarsSource.RequestTimeout）。
// 再試行の既定は 1 試行（規則は TradeDecisionGrpcCalls を共有）。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。**
public sealed class OrderExecutionGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://order-execution-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "OrderExecution:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "OrderExecution:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "OrderExecution:GrpcMaxAttempts";

    /// <summary>REST の "order-execution" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = HttpDailyBarsSource.RequestTimeout;

    private readonly GrpcChannel _channel;
    private readonly TradeDecisionGrpcCalls _calls;

    public OrderExecutionGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<OrderExecutionGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _calls = new TradeDecisionGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Client = new Proto.OrderExecutionRead.OrderExecutionReadClient(channel);
    }

    internal Proto.OrderExecutionRead.OrderExecutionReadClient Client { get; }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    /// <summary>1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。</summary>
    internal Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.OrderExecutionRead.OrderExecutionReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class =>
        _calls.CallAsync(operation, fallback, options => call(Client, options), cancellationToken);

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0559 決定 3: 構成から輸送を組む登録点（段 2 の RiskManagementGrpcExtensions と同じ規則）。
public static class OrderExecutionGrpcExtensions
{
    /// <summary><c>OrderExecution:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。</summary>
    public static IServiceCollection AddAiStockTradingOrderExecutionGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = TradeDecisionGrpcCalls.ResolveAddress(config, OrderExecutionGrpcTransport.AddressKey);
        if (address is null)
            return services;

        var timeout = TradeDecisionGrpcCalls.ReadTimeout(
            config, OrderExecutionGrpcTransport.TimeoutKey, OrderExecutionGrpcTransport.DefaultTimeout);
        var maxAttempts = TradeDecisionGrpcCalls.ReadMaxAttempts(config, OrderExecutionGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp => new OrderExecutionGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の "order-execution" HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<OrderExecutionGrpcTransport>>()));
        return services;
    }
}

// NFR, IADR-0427 決定 3: 線上表現 ⇄ 本サービスの型（受け手側の写し）。
// 🔴 **原則 A**: 欠落・未指定・未知は `null`（不明）へ写す。市場・方向・状態は**名前で**写す（C# の 0＝日本・買い・Unknown を線上の 0＝未指定と
// 取り違えない）。写した先は REST の受け手と同じ nullable の DTO であり、解釈は REST の `Interpret` が持つ。
internal static class OrderExecutionWire
{
    internal static Proto.Market ToProto(Market market) => market switch
    {
        AiStockTrading.Shared.Contracts.Trading.Market.Japan => Proto.Market.Japan,
        AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates => Proto.Market.UnitedStates,
        _ => Proto.Market.Unspecified,
    };

    internal static Proto.TradeSide ToProto(TradeSide side) => side switch
    {
        TradeSide.Buy => Proto.TradeSide.Buy,
        TradeSide.Sell => Proto.TradeSide.Sell,
        _ => Proto.TradeSide.Unspecified,
    };

    internal static Market? Market(Proto.Market value) => value switch
    {
        Proto.Market.Japan => AiStockTrading.Shared.Contracts.Trading.Market.Japan,
        Proto.Market.UnitedStates => AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates,
        _ => null,
    };

    internal static TradeSide? Side(Proto.TradeSide value) => value switch
    {
        Proto.TradeSide.Buy => TradeSide.Buy,
        Proto.TradeSide.Sell => TradeSide.Sell,
        _ => null,
    };

    // 送り手 DailyBarsStatus の数値（REST の本文の数値と同じ）。UNSPECIFIED・未知は null（＝状態の欠落）。
    internal static int? Status(Proto.DailyBarsState value) => value switch
    {
        Proto.DailyBarsState.Unavailable => HttpDailyBarsSource.StatusUnavailable,
        Proto.DailyBarsState.Available => HttpDailyBarsSource.StatusAvailable,
        _ => null,
    };

    // 送り手 InFlightClosesStatus の数値（REST の本文の数値と同じ）。UNSPECIFIED・未知は null（＝状態の欠落）。
    internal static int? Status(Proto.InFlightClosesState value) => value switch
    {
        Proto.InFlightClosesState.Unknown => HttpInFlightCloseProvider.StatusUnknown,
        Proto.InFlightClosesState.Covered => HttpInFlightCloseProvider.StatusCovered,
        Proto.InFlightClosesState.NotCovered => HttpInFlightCloseProvider.StatusNotCovered,
        _ => null,
    };

    internal static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // 日付・価格は読めなければ null（＝欄の欠け。1 本でも欠ければ REST の Interpret が応答ごと読まない）。
    internal static DateOnly? Day(bool has, string value) =>
        has && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    internal static decimal? Price(bool has, string value) =>
        has && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
