using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-10, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 2）, IADR-0328, IADR-0331, IADR-0427 決定 5, #997 (#753):
// リスク管理の読み取り（`aistocktrading.riskmanagement.v1.RiskControlsRead`）を呼ぶ**本サービスの**輸送。
// 構成 `RiskManagement:Grpc`（宛先）を宣言したときだけ DI に載り、載っていればサイジング文脈・保有建玉のポートが
// `Grpc*` 実装を選ぶ（**既定は REST**。宣言しなければ本ファイルは何も登録しない＝振る舞いは 1 つも変わらない）。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない）。** 本サービスは段 1 の全体前提条件が
// `GrpcChannel` を singleton で登録し `GetRequiredService<GrpcChannel>()` で引く（AssumptionsClientExtensions）。
// ここで同じ型をもう 1 つ登録すると、後勝ちで**前提条件の照会がリスク管理の宛先へ飛ぶ**（例外にならず、
// UNIMPLEMENTED → 安全既定へ倒れるだけなので気付けない）。
//
// タイムアウトと再試行は段 1（GrpcAssumptionsClient / IADR-0331 決定 3）と同じ規則（実体は段 4 の輸送と共有する
// `TradeDecisionGrpcCalls`。IADR-0446 決定 5・#1061）:
//   - timeout: **試行ごとの** `CallOptions.Deadline`。既定は REST の "risk" HttpClient.Timeout と同値（5 秒）。
//   - retry: 既定 1 試行（＝再試行しない＝REST と同じ振る舞い）。再試行するのは `Unavailable` / `DeadlineExceeded` だけ。
//   - 失敗は `null`（取得できなかった）で返す。**何へ倒すかは各ポートが持つ**（不明・残枠 0 の安全既定は経路ごとに違う。
//     IADR-0328 決定 5「共通の fail-safe ヘルパは書かない」）。ここが共通化するのは輸送の規則だけである。
public sealed class RiskManagementGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://risk-management-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "RiskManagement:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "RiskManagement:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "RiskManagement:GrpcMaxAttempts";

    /// <summary>REST の "risk" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly GrpcChannel _channel;
    private readonly TradeDecisionGrpcCalls _calls;

    public RiskManagementGrpcTransport(
        GrpcChannel channel, TimeSpan timeout, int maxAttempts, ILogger<RiskManagementGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        // NFR, IADR-0446 決定 5, #1061: deadline・再試行の規則は段 4 の輸送と共有する（TradeDecisionGrpcCalls）。
        _calls = new TradeDecisionGrpcCalls(timeout, maxAttempts, logger ?? throw new ArgumentNullException(nameof(logger)));
        Client = new Proto.RiskControlsRead.RiskControlsReadClient(channel);
    }

    internal Proto.RiskControlsRead.RiskControlsReadClient Client { get; }

    internal TimeSpan Timeout => _calls.Timeout;

    internal int MaxAttempts => _calls.MaxAttempts;

    internal static bool IsRetryable(StatusCode status) => TradeDecisionGrpcCalls.IsRetryable(status);

    /// <summary>
    /// 1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。
    /// </summary>
    /// <param name="operation">ログに載せる照会の名前。</param>
    /// <param name="fallback">失敗時に呼び出し元が倒す先（ログに載せる）。</param>
    internal Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.RiskControlsRead.RiskControlsReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class =>
        _calls.CallAsync(operation, fallback, options => call(Client, options), cancellationToken);

    public void Dispose() => _channel.Dispose();
}

// NFR, IADR-0427 決定 5: 構成から輸送を組む登録点（段 1 の AssumptionsClientExtensions と同じ規則）。
public static class RiskManagementGrpcExtensions
{
    /// <summary>
    /// <c>RiskManagement:Grpc</c> が宣言されていれば輸送を singleton で登録する。宣言が無ければ**何もしない**（既定は REST）。
    /// </summary>
    public static IServiceCollection AddAiStockTradingRiskManagementGrpc(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var address = ResolveAddress(config);
        if (address is null)
            return services;

        var timeout = TradeDecisionGrpcCalls.ReadTimeout(
            config, RiskManagementGrpcTransport.TimeoutKey, RiskManagementGrpcTransport.DefaultTimeout);
        var maxAttempts = TradeDecisionGrpcCalls.ReadMaxAttempts(config, RiskManagementGrpcTransport.MaxAttemptsKey);
        services.AddSingleton(sp => new RiskManagementGrpcTransport(
            GrpcClientExtensions.CreateAiStockTradingChannel(
                address.AbsoluteUri,
                // REST の "risk" HttpClient（AddAiStockTradingServiceToken）と同じ供給元。未整備なら共有の no-op
                // （IADR-0332 決定 6。null 実装を呼び出し元ごとに書かない）。
                sp.GetService<IServiceAccessTokenProvider>() ?? NoServiceAccessTokenProvider.Instance),
            timeout,
            maxAttempts,
            sp.GetRequiredService<ILogger<RiskManagementGrpcTransport>>()));
        return services;
    }

    /// <summary>未宣言（未設定・空白）は <c>null</c>＝REST。</summary>
    /// <remarks>
    /// 🔴 **宣言してあるのに使えない値は起動時に落とす**（段 1 の <c>Configuration:Grpc</c>・IADR-0331 決定 4 と同じ）。
    /// 黙って REST へ戻すと「gRPC へ切り替えたつもりで切り替わっていない」が綴り誤りと区別できない。
    /// </remarks>
    internal static Uri? ResolveAddress(IConfiguration config) =>
        TradeDecisionGrpcCalls.ResolveAddress(config, RiskManagementGrpcTransport.AddressKey);
}

// NFR, IADR-0427 決定 3: 線上表現 → 本サービスの型（受け手側の写し）。
//
// 🔴 **原則 A**: 欠落・未指定は `null`（不明）へ写す。0・false・空・列挙の 0 へ写さない ——
// その先の解釈（REST と共有の行の検証。IADR-0427 決定 5）が「不明」と「無し」を分けるための前提である。
internal static class RiskManagementWire
{
    // 🔴 空・欠落は**不明**（段 1 の `"" → 0` とは違う。ここで運ぶ金額は REST の受け手が nullable で受けていた）。
    // 読めない文字列は FormatException（呼び出し元が「応答を解釈できない」へ倒す）。
    internal static decimal? Decimal(bool has, string value) =>
        has && !string.IsNullOrEmpty(value)
            ? decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)
            : null;

    internal static DateTimeOffset? Timestamp(bool has, string value) =>
        has && !string.IsNullOrEmpty(value)
            ? DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

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

    internal static BrokerProvider? Provider(Proto.BrokerProvider value) => value switch
    {
        Proto.BrokerProvider.InternalPaper => BrokerProvider.InternalPaper,
        Proto.BrokerProvider.MoomooReal => BrokerProvider.MoomooReal,
        Proto.BrokerProvider.MoomooSimulate => BrokerProvider.MoomooSimulate,
        _ => null,
    };

    internal static StopLossExecutionMethod? StopLossMethod(Proto.StopLossExecutionMethod value) => value switch
    {
        Proto.StopLossExecutionMethod.BrokerStopOrder => StopLossExecutionMethod.BrokerStopOrder,
        Proto.StopLossExecutionMethod.SoftwareStop => StopLossExecutionMethod.SoftwareStop,
        Proto.StopLossExecutionMethod.NoProtectiveStop => StopLossExecutionMethod.NoProtectiveStop,
        Proto.StopLossExecutionMethod.AlternativeBrokerOrderType => StopLossExecutionMethod.AlternativeBrokerOrderType,
        _ => null,
    };
}
