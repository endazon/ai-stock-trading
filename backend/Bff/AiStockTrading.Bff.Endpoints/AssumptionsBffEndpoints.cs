using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;

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
}
