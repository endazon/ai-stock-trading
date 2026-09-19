using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.Extensions.Logging;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, NFR-05, #840, IADR-0352 決定 1・2: 報告書サービスが依存先（取引台帳・監査台帳・LLM ゲートウェイ）を
// 呼ぶ名前付き HttpClient の**最外**に挿すハンドラ。役割は 2 つ。
//
// 1. **門**: サービストークンを取得できないなら**送信しない**。
//    共有の `ServiceTokenHandler`（IADR-0051 決定 2）は取得不能のときヘッダ無しで送り、上流の 401 を経て
//    呼び出し側の fail-safe へ倒す。倒れ先（未供給）は同じだが、①結果の分かっている 401 を上流へ投げ、
//    ②「依存先がまだ起動していない」が「認可を拒否された」に化ける。後者は待っても直らない失敗であり、
//    見送って再試行する判定（決定 3）を誤らせる。報告書サービスでは**取得できない時点で未供給と決める**。
//    供給元（Http*Source）は既存の `catch (Exception)` で未供給へ倒れる（契約は変えない）。
//    トークンが取れたときは本ハンドラが付与する。内側の `ServiceTokenHandler` は既存の Authorization を
//    尊重するため二重取得しない。
//
//    🔴 **資格情報が未整備の構成では素通しする**（`tokenProvider` が null / `NoServiceAccessTokenProvider`）。
//    未整備は「取得に失敗した」ではなく、認証なしで動かす dev・単体実行の構成である（従来どおり）。
//
// 2. **観測**: 失敗を「待てば直り得る（一過性）」と「待っても直らない（恒常）」に分けて
//    `ReportDependencyProbe` へ記録する。分類の考え方は `GrpcAssumptionsClient.IsRetryable`
//    （Unavailable / DeadlineExceeded だけを再試行し、Unauthenticated / PermissionDenied は即座に倒す）と同じ。
//    応答・例外はそのまま呼び出し元へ返す（供給元の縮退とログは変えない）。
public sealed class ReportDependencyHandler(
    ReportDependencyProbe probe,
    IServiceAccessTokenProvider? tokenProvider,
    string dependency,
    bool timeoutIsTransient,
    ILogger<ReportDependencyHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RequiresToken && request.Headers.Authorization is null)
        {
            var token = await tokenProvider!.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                probe.Record(
                    dependency, ReportDependencyFailureKind.ServiceTokenUnavailable, transient: true,
                    "サービストークンを取得できない");
                logger.LogWarning(
                    "サービストークンを取得できないため、{Dependency} への要求を送信しません"
                    + "（認証なしでは送りません。{Method} {Path}）。未供給として扱います。",
                    dependency, request.Method, request.RequestUri?.AbsolutePath);
                throw new ServiceTokenUnavailableException(dependency);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // HttpClient.Timeout も呼び出し元の停止要求も、鎖の中では同じ取り消しに見える。
            // 停止要求なら生成器ごと中断されるため、ここでの記録は読まれない（害が無い）。
            probe.Record(dependency, ReportDependencyFailureKind.Timeout, timeoutIsTransient, "タイムアウト");
            throw;
        }
        catch (HttpRequestException ex)
        {
            probe.Record(
                dependency, ReportDependencyFailureKind.Unreachable, transient: true,
                ex.HttpRequestError.ToString());
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            probe.Record(
                dependency, ReportDependencyFailureKind.HttpStatus, IsTransient(status),
                ((int)status).ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    private bool RequiresToken => tokenProvider is not null and not NoServiceAccessTokenProvider;

    /// <summary>
    /// 待てば直り得る状態か。5xx（依存先・その手前のプロキシがまだ立ち上がっていない）・408・429。
    /// 🔴 **401 / 403 は入れない**——トークンを付けて拒否されたのなら、ロール未付与などの設定誤りであり、
    /// 待っても変わらない。ここへ足すと「沈黙せず縮退した報告書を出す」までの時間が伸びるだけになる。
    /// </summary>
    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
}

// FR-06, NFR-05, #840, IADR-0352 決定 1: サービストークンを取得できず、要求を**送信しなかった**。
// HttpRequestException 派生にしてあるのは、通信失敗を `HttpRequestException` で受ける呼び出し元が
// あっても同じ縮退へ倒れるようにするため（報告書サービスの供給元は `Exception` で受けている）。
public sealed class ServiceTokenUnavailableException(string dependency)
    : HttpRequestException($"サービストークンを取得できないため {dependency} への要求を送信しませんでした。")
{
    public string Dependency { get; } = dependency;
}
