using System.Globalization;
using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// NFR, FR-06, MSP:ADR-0029, IADR-0284 決定 5（段 2・段 3）, IADR-0331 決定 3・4, IADR-0352, IADR-0427 決定 4・6,
// IADR-0445 決定 5, #997 / #1059 (#753):
// **本サービスの** east-west gRPC の 1 回の照会の規則（依存先の門・観測・試行ごとの deadline・再試行）。
// 段 2（リスク管理・`risk-ledger`）の輸送が持っていた規則を、段 3（監査台帳・`audit-ledger`）の輸送と**共有する**ため切り出した
// （同じサービスの中で同じ規則を 2 つ書くと、片方だけが直る）。規則そのものは段 2 から 1 つも変えていない。
//
// 🔴 **REST の名前付き HttpClient が持つ依存先の門と観測（#840 / IADR-0352 決定 1・2）を gRPC でも同じに行う。**
// REST ではこれを `ReportDependencyHandler`（HttpClient の最外のハンドラ）が担う。gRPC はその鎖を通らないので、輸送を
// 差し替えただけでは門と観測が**黙って消える**:
//   1. **門**: 資格情報が整っているのにサービストークンを取得できないなら**送信しない**（未供給・一過性として記録）。
//   2. **観測**: 失敗を一過性／恒常へ分けて `ReportDependencyProbe` へ記録する。status は HTTP 相当の状態コードへ写し、
//      **REST と同じ判定**（`ReportDependencyHandler.IsTransient`）を使う —— 分類を 2 箇所に書かない。
//
// 🔴 サービスを跨いだ共通化はしない（`*.Client` を廃した裁定＝呼び出し元ごとの複製。IADR-0264 決定 1 / IADR-0427 決定 4）。
// **何へ倒すか（未供給・空列）は各供給元が持つ**（本型は「取れなかった＝null」までしか言わない。IADR-0328 決定 5）。
internal sealed class ReportGrpcCalls(
    string dependency,
    TimeSpan timeout,
    int maxAttempts,
    ReportDependencyProbe probe,
    IServiceAccessTokenProvider? tokenProvider,
    ILogger logger)
{
    private readonly string _dependency = dependency ?? throw new ArgumentNullException(nameof(dependency));
    private readonly ReportDependencyProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    internal string Dependency => _dependency;

    internal TimeSpan Timeout { get; } = timeout;

    internal int MaxAttempts { get; } = maxAttempts < 1 ? 1 : maxAttempts;

    internal static bool IsRetryable(StatusCode status) =>
        status is StatusCode.Unavailable or StatusCode.DeadlineExceeded;

    // 資格情報が未整備の構成（null / no-op）では門は素通しする（REST の ReportDependencyHandler.RequiresToken と同じ）。
    private bool RequiresToken => tokenProvider is not null and not NoServiceAccessTokenProvider;

    /// <summary>
    /// 1 回の照会（再試行を含む）。取得できなければ <c>null</c>。呼び出し元自身のキャンセルは伝播させる。
    /// </summary>
    internal async Task<TResponse?> CallAsync<TResponse>(
        string operation,
        string fallback,
        Func<CallOptions, AsyncUnaryCall<TResponse>> call,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        for (var attempt = 1; ; attempt++)
        {
            // 1. 門（試行ごと。供給元はトークンをキャッシュしているので、チャネルの資格情報と二重に取っても要求は増えない）。
            if (RequiresToken)
            {
                var token = await tokenProvider!.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(token))
                {
                    _probe.Record(
                        _dependency, ReportDependencyFailureKind.ServiceTokenUnavailable, transient: true,
                        "サービストークンを取得できない");
                    _logger.LogWarning(
                        "サービストークンを取得できないため、{Dependency} への gRPC 照会（{Operation}）を送信しません"
                        + "（認証なしでは送りません）。{Fallback}",
                        _dependency, operation, fallback);
                    return null;
                }
            }

            try
            {
                using var rpc = call(
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
                _probe.Record(_dependency, ReportDependencyFailureKind.Timeout, transient: true, "タイムアウト");
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
                _probe.Record(_dependency, ReportDependencyFailureKind.Unreachable, transient: true, status.ToString());
                break;
            case StatusCode.DeadlineExceeded:
                _probe.Record(_dependency, ReportDependencyFailureKind.Timeout, transient: true, "タイムアウト");
                break;
            default:
                var http = EquivalentHttpStatus(status);
                _probe.Record(
                    _dependency, ReportDependencyFailureKind.GrpcStatus, ReportDependencyHandler.IsTransient(http),
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

    // ---- 構成の読み方（段 1 の AssumptionsClientExtensions と同じ規則。宛先のキーだけが輸送ごとに違う） ----

    /// <summary>未宣言（未設定・空白）は <c>null</c>＝REST。宣言してあるのに使えない値は起動時に落とす（IADR-0331 決定 4 と同じ）。</summary>
    internal static Uri? ResolveAddress(IConfiguration config, string addressKey)
    {
        var raw = config[addressKey];
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"{addressKey} は絶対 URL である必要があります（実際の値: \"{raw}\"）。");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{addressKey} の scheme は http のみです（実際の値: \"{raw}\"）。"
                + " メッシュ内の TLS はサイドカーが終端します。");

        return uri;
    }

    internal static TimeSpan ReadTimeout(IConfiguration config, string timeoutKey, TimeSpan fallback) =>
        int.TryParse(config[timeoutKey], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;

    internal static int ReadMaxAttempts(IConfiguration config, string maxAttemptsKey) =>
        int.TryParse(config[maxAttemptsKey], out var attempts) && attempts > 1 ? attempts : 1;
}
