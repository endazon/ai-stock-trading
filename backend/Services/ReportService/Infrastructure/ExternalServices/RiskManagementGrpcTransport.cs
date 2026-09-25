using System.Globalization;
using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReportService.Features.Reports;
using Proto = AiStockTrading.Shared.Grpc.RiskManagement.V1;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 2）, IADR-0328, IADR-0331, IADR-0352, IADR-0427 決定 5・6,
// #997 (#753):
// リスク管理の読み取り（`aistocktrading.riskmanagement.v1.RiskControlsRead`）を呼ぶ**本サービスの**輸送。
// 構成 `RiskManagement:Grpc`（宛先）を宣言したときだけ DI に載り、載っていれば取引台帳の 6 つの供給元が `Grpc*` 実装を選ぶ
// （**既定は REST**。宣言しなければ本ファイルは何も登録しない＝振る舞いは 1 つも変わらない）。
//
// 🔴 **REST の `risk-ledger` が持つ依存先の門と観測（#840 / IADR-0352 決定 1・2）を gRPC でも同じに行う。**
// REST ではこれを `ReportDependencyHandler`（HttpClient の最外のハンドラ）が担っていた。gRPC はその鎖を通らないので、
// 輸送を差し替えただけでは門と観測が**黙って消える**（例外も出ず、テストも緑のまま「再起動直後の縮退した報告書」が
// 確定まで進む ＝ #840 で直した事故の再発になる）。したがってここで同じ判定を行う:
//   1. **門**: 資格情報が整っているのにサービストークンを取得できないなら**送信しない**（未供給・一過性として記録）。
//   2. **観測**: 失敗を一過性／恒常へ分けて `ReportDependencyProbe` へ記録する。status は HTTP 相当の状態コードへ写し、
//      **REST と同じ判定**（`ReportDependencyHandler.IsTransient`）を使う —— 分類を 2 箇所に書かない。
//
// タイムアウトと再試行は段 1（IADR-0331 決定 3）と同じ規則。deadline の既定は REST の `risk-ledger` の
// HttpClient.Timeout と同値（10 秒）。再試行の既定は 1 試行。
//
// 🔴 **チャネルは本型が所有する（`GrpcChannel` を DI へ裸で登録しない）。** 他の面が同じ型を引くと後勝ちで宛先が入れ替わる。
public sealed class RiskManagementGrpcTransport : IDisposable
{
    /// <summary>gRPC の宛先（例 <c>http://risk-management-service:8081</c>）。**未設定なら REST**。</summary>
    internal const string AddressKey = "RiskManagement:Grpc";

    /// <summary>試行ごとの deadline（秒）。未設定は REST の HttpClient.Timeout と同値。</summary>
    internal const string TimeoutKey = "RiskManagement:GrpcTimeoutSeconds";

    /// <summary>試行回数。未設定は 1（＝再試行しない）。</summary>
    internal const string MaxAttemptsKey = "RiskManagement:GrpcMaxAttempts";

    /// <summary>観測に載せる依存先の名前（REST の名前付き HttpClient と同じ）。</summary>
    internal const string Dependency = "risk-ledger";

    /// <summary>REST の "risk-ledger" HttpClient.Timeout と同値。</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly GrpcChannel _channel;
    private readonly ReportDependencyProbe _probe;
    private readonly IServiceAccessTokenProvider? _tokenProvider;
    private readonly ILogger<RiskManagementGrpcTransport> _logger;

    public RiskManagementGrpcTransport(
        GrpcChannel channel,
        TimeSpan timeout,
        int maxAttempts,
        ReportDependencyProbe probe,
        IServiceAccessTokenProvider? tokenProvider,
        ILogger<RiskManagementGrpcTransport> logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _tokenProvider = tokenProvider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Client = new Proto.RiskControlsRead.RiskControlsReadClient(channel);
        Timeout = timeout;
        MaxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
    }

    internal Proto.RiskControlsRead.RiskControlsReadClient Client { get; }

    internal TimeSpan Timeout { get; }

    internal int MaxAttempts { get; }

    internal static bool IsRetryable(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    // 資格情報が未整備の構成（null / no-op）では門は素通しする（REST の ReportDependencyHandler.RequiresToken と同じ）。
    private bool RequiresToken => _tokenProvider is not null and not NoServiceAccessTokenProvider;

    /// <summary>
    /// 1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。
    /// </summary>
    internal async Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<Proto.RiskControlsRead.RiskControlsReadClient, CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        for (var attempt = 1; ; attempt++)
        {
            // 1. 門（試行ごと。供給元はトークンをキャッシュしているので、チャネルの資格情報と二重に取っても要求は増えない）。
            if (RequiresToken)
            {
                var token = await _tokenProvider!.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                {
                    _probe.Record(
                        Dependency, ReportDependencyFailureKind.ServiceTokenUnavailable, transient: true,
                        "サービストークンを取得できない");
                    _logger.LogWarning(
                        "サービストークンを取得できないため、{Dependency} への gRPC 照会（{Operation}）を送信しません"
                        + "（認証なしでは送りません）。{Fallback}",
                        Dependency, operation, fallback);
                    return null;
                }
            }

            try
            {
                using var rpc = call(
                    Client,
                    new CallOptions(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: cancellationToken));
                return await rpc.ResponseAsync.ConfigureAwait(false);
            }
            catch (RpcException ex)
                when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (RpcException ex)
            {
                // 2. 観測（試行ごと。REST は要求ごとに記録する）。
                RecordFailure(ex.StatusCode);

                if (IsRetryable(ex.StatusCode) && attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        "{Operation} の gRPC 照会に失敗（{Status}・{Attempt}/{Attempts} 回目）。再試行します。",
                        operation, ex.StatusCode, attempt, MaxAttempts);
                    continue;
                }

                _logger.LogWarning(
                    "{Operation} の gRPC 照会に失敗（{Status}）。{Fallback}", operation, ex.StatusCode, fallback);
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _probe.Record(Dependency, ReportDependencyFailureKind.Timeout, transient: true, "タイムアウト");
                _logger.LogWarning("{Operation} の gRPC 照会がタイムアウト。{Fallback}", operation, fallback);
                return null;
            }
        }
    }

    // REST と同じ分類を使う（分類を 2 箇所に書かない）。接続できない＝Unreachable、deadline＝Timeout（いずれも一過性）。
    private void RecordFailure(StatusCode status)
    {
        switch (status)
        {
            case StatusCode.Unavailable:
                _probe.Record(Dependency, ReportDependencyFailureKind.Unreachable, transient: true, status.ToString());
                break;
            case StatusCode.DeadlineExceeded:
                _probe.Record(Dependency, ReportDependencyFailureKind.Timeout, transient: true, "タイムアウト");
                break;
            default:
                var http = EquivalentHttpStatus(status);
                _probe.Record(
                    Dependency, ReportDependencyFailureKind.GrpcStatus, ReportDependencyHandler.IsTransient(http),
                    string.Create(CultureInfo.InvariantCulture, $"{status}（HTTP 相当 {(int)http}）"));
                break;
        }
    }

    /// <summary>
    /// gRPC の status を HTTP 相当の状態コードへ写す（gRPC の公式の対応表）。一過性の判定を REST と共有するためだけに使う。
    /// 🔴 <c>Unauthenticated</c> は 401（＝一過性。#866）、<c>PermissionDenied</c> は 403（＝恒常）になる。
    /// </summary>
    internal static HttpStatusCode EquivalentHttpStatus(StatusCode status) => status switch
    {
        StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.OutOfRange => HttpStatusCode.BadRequest,
        StatusCode.Unauthenticated => HttpStatusCode.Unauthorized,
        StatusCode.PermissionDenied => HttpStatusCode.Forbidden,
        StatusCode.NotFound => HttpStatusCode.NotFound,
        StatusCode.Aborted or StatusCode.AlreadyExists => HttpStatusCode.Conflict,
        StatusCode.ResourceExhausted => HttpStatusCode.TooManyRequests,
        StatusCode.Unimplemented => HttpStatusCode.NotImplemented,
        StatusCode.Unavailable => HttpStatusCode.ServiceUnavailable,
        StatusCode.DeadlineExceeded => HttpStatusCode.GatewayTimeout,
        _ => HttpStatusCode.InternalServerError,
    };

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

        var timeout = ReadTimeout(config);
        var maxAttempts = ReadMaxAttempts(config);
        services.AddSingleton(sp =>
        {
            // REST の risk-ledger（AddReportDependencyGate ＋ AddAiStockTradingServiceToken）と同じ AST レルムの供給元。
            var tokenProvider = sp.GetService<IServiceAccessTokenProvider>();
            return new RiskManagementGrpcTransport(
                GrpcClientExtensions.CreateAiStockTradingChannel(
                    address.AbsoluteUri, tokenProvider ?? NoServiceAccessTokenProvider.Instance),
                timeout,
                maxAttempts,
                sp.GetRequiredService<ReportDependencyProbe>(),
                tokenProvider,
                sp.GetRequiredService<ILogger<RiskManagementGrpcTransport>>());
        });
        return services;
    }

    /// <summary>未宣言（未設定・空白）は <c>null</c>＝REST。宣言してあるのに使えない値は起動時に落とす（IADR-0331 決定 4 と同じ）。</summary>
    internal static Uri? ResolveAddress(IConfiguration config)
    {
        var raw = config[RiskManagementGrpcTransport.AddressKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"{RiskManagementGrpcTransport.AddressKey} は絶対 URL である必要があります（実際の値: \"{raw}\"）。");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{RiskManagementGrpcTransport.AddressKey} の scheme は http のみです（実際の値: \"{raw}\"）。"
                + " メッシュ内の TLS はサイドカーが終端します。");

        return uri;
    }

    private static TimeSpan ReadTimeout(IConfiguration config) =>
        int.TryParse(config[RiskManagementGrpcTransport.TimeoutKey], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : RiskManagementGrpcTransport.DefaultTimeout;

    private static int ReadMaxAttempts(IConfiguration config) =>
        int.TryParse(config[RiskManagementGrpcTransport.MaxAttemptsKey], out var attempts) && attempts > 1 ? attempts : 1;
}

// NFR, IADR-0427 決定 3: 線上表現 → 本サービスの型（受け手側の写し）。
//
// 🔴 **原則 A**: 欠落・未指定は `null`（不明）へ写す。0・false・空・列挙の 0 へ写さない。
// 読めない文字列（10 進・日付・時刻・GUID）は FormatException（呼び出し元が「応答を解釈できない」へ倒す）。
internal static class RiskManagementWire
{
    internal static decimal? Decimal(bool has, string value) =>
        has && !string.IsNullOrEmpty(value)
            ? decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)
            : null;

    internal static DateTimeOffset? Timestamp(bool has, string value) =>
        has && !string.IsNullOrEmpty(value)
            ? DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;

    internal static DateOnly? Day(bool has, string value) =>
        has && !string.IsNullOrEmpty(value)
            ? DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    internal static DateOnly Day(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static Guid? Id(bool has, string value) =>
        has && !string.IsNullOrEmpty(value) ? Guid.ParseExact(value, "D") : null;

    internal static string Wire(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    internal static PositionEffect? Effect(Proto.PositionEffect value) => value switch
    {
        Proto.PositionEffect.Open => PositionEffect.Open,
        Proto.PositionEffect.Close => PositionEffect.Close,
        _ => null,
    };

    internal static BrokerProvider? Provider(Proto.BrokerProvider value) => value switch
    {
        Proto.BrokerProvider.InternalPaper => BrokerProvider.InternalPaper,
        Proto.BrokerProvider.MoomooReal => BrokerProvider.MoomooReal,
        Proto.BrokerProvider.MoomooSimulate => BrokerProvider.MoomooSimulate,
        _ => null,
    };

    internal static TradingStage? Stage(Proto.TradingStage value) => value switch
    {
        Proto.TradingStage.Stage0Verification => TradingStage.Stage0Verification,
        Proto.TradingStage.Stage1Simulate => TradingStage.Stage1Simulate,
        Proto.TradingStage.Stage2MinimalLive => TradingStage.Stage2MinimalLive,
        Proto.TradingStage.Stage3ScaledLive => TradingStage.Stage3ScaledLive,
        _ => null,
    };
}
