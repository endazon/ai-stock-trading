using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace AiStockTrading.Bff.Endpoints;

// Issue #283, FR-17, UC-06, IADR-0070: AST 設定画面（全体前提条件の閲覧/変更）の BFF 集約。
// ConfigurationService（/assumptions・/assumptions/history）へ pass-through プロキシする。
//
// 認可は後段（OwnerOrService / OwnerOnly）が強制する。本 BFF は認証（匿名は 401）と利用者トークンの
// 伝播のみを担い、後段のステータス（200/400/403/404/409 等）と本文・Content-Type をそのまま透過する。
// DTO には結合しない（platform → 可変ユニット参照は禁止・IADR-0057。よって型付けせず素通し）。
// 後段不達は 502 へ縮退する（fail-safe）。フロントの存在秘匿（RequireRole→NotFound）はサーバ 401/403 の
// 表示側バックストップ（IADR-0009/0035）。
public static class AssumptionsBffEndpoints
{
    // 後段の名前付き HTTP クライアント（BaseAddress は Program.cs で Services:ConfigurationService から設定）。
    internal const string ClientName = "ConfigurationService";

    public static IEndpointRouteBuilder MapAssumptionsBffEndpoints(this IEndpointRouteBuilder app)
    {
        // グループは認証必須（匿名は 401）。owner 判定は後段に委ねる。
        var g = app.MapGroup("/bff/assumptions")
            .WithTags("Assumptions BFF")
            .RequireAuthorization();

        // 現在の全体前提条件（バージョン付き）。後段 OwnerOrService。
        g.MapGet("", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/assumptions", ct))
            .WithName("BffAssumptionsGet");

        // 変更履歴（新しい順）。後段 OwnerOnly。
        g.MapGet("/history", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/assumptions/history", ct))
            .WithName("BffAssumptionsHistory");

        // 変更（楽観排他 ExpectedVersion＋理由必須は後段が検証。400/409 はそのまま透過）。後段 OwnerOnly。
        g.MapPut("", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Put, "/assumptions", ct))
            .WithName("BffAssumptionsPut");

        return app;
    }

    // 後段 ConfigurationService へ pass-through する。ステータス・本文・Content-Type を透過し、
    // 利用者トークンを伝播する。PUT はリクエスト本文をそのまま転送する。後段不達は 502。
    private static async Task<IResult> ProxyAsync(
        IHttpClientFactory httpFactory, HttpContext http, HttpMethod method, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(method, path);

        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        // 本文を持つメソッド（PUT）はリクエスト本文をそのまま後段へ転送する。
        if (HttpMethods.IsPut(method.Method) || HttpMethods.IsPost(method.Method) || HttpMethods.IsPatch(method.Method))
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            var content = new ByteArrayContent(buffer.ToArray());
            var contentType = http.Request.ContentType;
            content.Headers.TryAddWithoutValidation(
                "Content-Type", string.IsNullOrEmpty(contentType) ? "application/json" : contentType);
            req.Content = content;
        }

        try
        {
            using var resp = await client.SendAsync(req, ct);

            // Issue #728, FR-17, UC-06, SC-01, IADR-0326: 上流 401 は「利用者未認証」ではなく
            // BFF↔上流間の資格情報・構成不整合である（本 BFF はグループで RequireAuthorization 済み）。
            // 透過すると SPA の apiFetch が「セッション失効」と誤認し再ログインの無限ループになるため、
            // 既存の「後段不達は 502」fail-safe へ合流させる。403（権限不足）は変更せず透過する。
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return UpstreamUnauthenticatedToBadGateway(http, resp, path);

            // 応答本文は ReadAsStringAsync で一括読み込みし Results.Content で透過する（AuthzBffEndpoints と同方式）。
            // 全体前提条件は小さな管理系ペイロードのためバッファ方式で足りる。ストリーミング（SSE）が要る
            // エンドポイント（AnalysisBff）とは異なり低レベルの Response.Body 直書きは用いない。
            var body = await resp.Content.ReadAsStringAsync(ct);
            var respContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            return Results.Content(body, respContentType, statusCode: (int)resp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 後段不達・タイムアウトは 502 へ縮退する（利用者のキャンセルは除外）。
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    // Issue #728, IADR-0326: 上流 401 を 502 へ写像する。警告ログには上流パスと WWW-Authenticate の
    // error/error_description のみを残し、Authorization ヘッダ・トークンは一切出さない。
    // 上流の応答本文は読まない（読むと後段の 401 本文がそのまま漏れる経路が増える）。
    private static IResult UpstreamUnauthenticatedToBadGateway(HttpContext http, HttpResponseMessage resp, string path)
    {
        var (error, description) = ParseWwwAuthenticateChallenge(resp);
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AiStockTrading.Bff.Endpoints.AssumptionsBffEndpoints")
            .LogWarning(
                "BFF proxy: upstream {UpstreamPath} returned 401 (mapped to 502). WWW-Authenticate error={Error} error_description={ErrorDescription}",
                path, error ?? "(none)", description ?? "(none)");
        return Results.Json(new { reason = "upstream_unauthenticated" }, statusCode: StatusCodes.Status502BadGateway);
    }

    // WWW-Authenticate の error / error_description パラメータを取り出す（RFC 6750 Bearer チャレンジの一般形）。
    // 読めなければ null を返し、ログの欠落を許容する（本流は止めない）。
    private static (string? Error, string? Description) ParseWwwAuthenticateChallenge(HttpResponseMessage resp)
    {
        var value = resp.Headers.WwwAuthenticate.Select(h => h.Parameter).FirstOrDefault(p => !string.IsNullOrEmpty(p));
        if (string.IsNullOrEmpty(value)) return (null, null);

        var errorMatch = Regex.Match(value, "error=\"([^\"]*)\"");
        var descriptionMatch = Regex.Match(value, "error_description=\"([^\"]*)\"");
        return (
            errorMatch.Success ? errorMatch.Groups[1].Value : null,
            descriptionMatch.Success ? descriptionMatch.Groups[1].Value : null);
    }
}
