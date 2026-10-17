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

namespace BacktestService.Infrastructure.ExternalServices;

// NFR, FR-15, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 5 の追補 2）, IADR-0427 決定 4・5, IADR-0528 決定8, IADR-0559 決定 3・4, #1391 (#753):
// 発注執行の読み取り（`aistocktrading.orderexecution.v1.OrderExecutionRead`）を呼ぶ**本サービスの**輸送。
// 構成 `OrderExecution:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば過去データ源 `order-execution` が
// `GrpcOrderExecutionHistoricalBarSource` を選ぶ（**既定は REST**。宣言が無ければ本ファイルは何も登録しない）。
// deadline の既定は REST の発注執行の HttpClient.Timeout と同値（30 秒）。再試行の既定は 1 試行。
//
// 🔴 **失敗は送出する（他サービスの輸送の「null で返す」とは違う）。** 本サービスの REST の源は通信例外・タイムアウトを握りつぶさず
// 送出し（Stooq・moomoo の源と同じ）、非 2xx だけをその銘柄の欠測にする。gRPC も同じ分け方にするため、最後の試行の `RpcException` を
// そのまま投げ、どれを欠測にするかは源（GrpcOrderExecutionHistoricalBarSource）が持つ（IADR-0328 決定 5「何へ倒すかは各ポートが持つ」）。
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない。IADR-0427 決定 4）。**
public sealed class OrderExecutionGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://order-execution-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "OrderExecution:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "OrderExecution:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "OrderExecution:GrpcMaxAttempts";

    /// <summary>REST の発注執行の HttpClient.Timeout と同値（Program.cs の OrderExecutionBarsHttpClientName）。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly GrpcChannel _channel;
    private readonly ILogger _logger;

    public OrderExecutionGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<OrderExecutionGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Timeout = timeout;
        MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        Client = new Proto.OrderExecutionRead.OrderExecutionReadClient(channel);
    }

    internal Proto.OrderExecutionRead.OrderExecutionReadClient Client { get; }

    internal TimeSpan Timeout { get; }

    internal int MaxAttempts { get; }

    /// <summary>送り手まで届かなかった・待ちきれなかった（再試行の対象）。REST の通信例外・タイムアウトに当たる。</summary>
    internal static bool IsTransportFailure(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    /// <summary>
    /// 1 回の照会（再試行を含む）。最後の試行の <see cref="RpcException"/> は送出する。呼び出し元自身のキャンセルは
    /// <see cref="OperationCanceledException"/> で伝播させる。
    /// </summary>
    internal async Task<TResponse> CallAsync<TResponse>(
        string operation,
        Func<Proto.OrderExecutionRead.OrderExecutionReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // AsyncUnaryCall は IDisposable（再試行のたびに新しい RPC を張るので、握ったままにしない）。
                using var rpc = call(
                    Client, new CallOptions(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: cancellationToken));
                return await rpc.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException ex)
                when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (RpcException ex) when (IsTransportFailure(ex.StatusCode) && attempt < MaxAttempts)
            {
                _logger.LogWarning(
                    "{Operation} の gRPC 照会に失敗（{Status}・{Attempt}/{Attempts} 回目）。再試行します。",
                    operation, ex.StatusCode, attempt, MaxAttempts);
            }
        }
    }

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0559 決定 3: 構成から輸送を組む登録点（他サービスの輸送と同じ構成の読み方）。
public static class OrderExecutionGrpcExtensions
{
    /// <summary><c>OrderExecution:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。</summary>
    public static IServiceCollection AddAiStockTradingOrderExecutionGrpc(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = ResolveAddress(config);
        if (address is null)
            return services;

        var timeout = int.TryParse(config[OrderExecutionGrpcTransport.TimeoutKey], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : OrderExecutionGrpcTransport.DefaultTimeout;
        var maxAttempts = int.TryParse(config[OrderExecutionGrpcTransport.MaxAttemptsKey], out var attempts) && attempts > 1 ? attempts : 1;
        services.AddSingleton(sp => new OrderExecutionGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の発注執行の HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<OrderExecutionGrpcTransport>>()));
        return services;
    }

    /// <summary>宣言があるか（値の検証は <see cref="ResolveAddress"/>）。過去データ源の選択（ResolveProvider）が読む。</summary>
    public static bool IsDeclared(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config[OrderExecutionGrpcTransport.AddressKey]);

    /// <summary>未宣言（未設定・空白）は <c>null</c>＝REST。</summary>
    /// <remarks>
    /// 🔴 **宣言してあるのに使えない値は起動時に落とす**（段 1 の <c>Configuration:Grpc</c>・IADR-0331 決定 4 と同じ）。
    /// 黙って REST へ戻すと「gRPC へ切り替えたつもりで切り替わっていない」が綴り誤りと区別できない。
    /// </remarks>
    internal static Uri? ResolveAddress(IConfiguration config)
    {
        var key = OrderExecutionGrpcTransport.AddressKey;
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"{key} は絶対 URL である必要があります（実際の値: \"{raw}\"）。");

        // メッシュ内は平文 h2c（TLS はサイドカーが終端する）。
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{key} の scheme は http のみです（実際の値: \"{raw}\"）。 メッシュ内の TLS はサイドカーが終端します。");

        return uri;
    }
}

// NFR, IADR-0427 決定 3: 線上表現 ⇄ 本サービスの型（受け手側の写し）。
// 🔴 **原則 A**: 欠落・未指定・未知は `null`（不明）へ写す。市場・状態は**名前で**写す。写した先は REST の源と同じ nullable の DTO
// （市場・状態は送り手の列挙の数値）であり、解釈は REST の `Interpret` が持つ。
internal static class OrderExecutionWire
{
    internal static Proto.Market ToProto(Market market) => market switch
    {
        Market.Japan => Proto.Market.Japan,
        Market.UnitedStates => Proto.Market.UnitedStates,
        _ => Proto.Market.Unspecified,
    };

    // 送り手 Market の数値（REST の本文の数値と同じ）。
    internal static int? MarketNumber(Proto.Market value) => value switch
    {
        Proto.Market.Japan => (int)Market.Japan,
        Proto.Market.UnitedStates => (int)Market.UnitedStates,
        _ => null,
    };

    // 送り手 DailyBarsStatus の数値（REST の本文の数値と同じ）。UNSPECIFIED・未知は null（＝状態の欠落）。
    internal static int? Status(Proto.DailyBarsState value) => value switch
    {
        Proto.DailyBarsState.Unavailable => HttpOrderExecutionHistoricalBarSource.StatusUnavailable,
        Proto.DailyBarsState.Available => HttpOrderExecutionHistoricalBarSource.StatusAvailable,
        _ => null,
    };

    internal static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // 日付・価格は読めなければ null（＝欄の欠け。1 本でも欠ければ REST の Interpret が銘柄ごと欠測にする）。
    internal static DateOnly? Day(bool has, string value) =>
        has && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    internal static decimal? Price(bool has, string value) =>
        has && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
