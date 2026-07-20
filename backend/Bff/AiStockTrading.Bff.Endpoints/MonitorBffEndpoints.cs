using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;

namespace AiStockTrading.Bff.Endpoints;

// Issue #288, FR-14, IADR-0072: AST 監視銘柄（SC-02 watchlist）の BFF 集約。
// MarketMonitorService（/monitor/*）へ pass-through プロキシする。後段認可は取得が OwnerOrService・変更が OwnerOnly（IADR-0095）。
//
// 認可は後段が強制する。SC-02 UI は利用者（trading-owner）トークンで叩き、owner は両ポリシーを満たす。本 BFF は認証（匿名は 401）と利用者トークンの伝播のみを担い、
// 後段のステータス（200/400/403/404/409 等）と本文・Content-Type をそのまま透過する。
// DTO には結合しない（platform → 可変ユニット参照は禁止・IADR-0057。よって型付けせず素通し）。
// 後段不達は 502 へ縮退する（fail-safe）。#287 の RiskControlsBffEndpoints と同型。
//
// 登録経路は SC-02 の watchlist UI が実消費する 4 本のみ（IADR-0072 決定2。/monitor/settings は
// フロントが叩かないため登録しない＝起こり得ない経路への防御的追加を避ける）。
// 差分: 後段 DELETE /monitor/watchlist は本文（銘柄・理由）を取るため、pass-through は DELETE でも
// リクエスト本文を後段へ転送する（IADR-0072 決定3）。
public static class MonitorBffEndpoints
{
    // 後段の名前付き HTTP クライアント（BaseAddress は Program.cs で Services:MarketMonitorService から設定）。
    internal const string ClientName = "MarketMonitorService";

    public static IEndpointRouteBuilder MapMonitorBffEndpoints(this IEndpointRouteBuilder app)
    {
        // グループは認証必須（匿名は 401）。認可判定は後段に委ねる（取得 OwnerOrService・変更 OwnerOnly）。
        var g = app.MapGroup("/bff/monitor")
            .WithTags("Monitor BFF")
            .RequireAuthorization();

        // SC-02 監視銘柄: 一覧取得（後段 OwnerOrService・IADR-0095）・追加・削除・変更履歴（後段 OwnerOnly）。
        g.MapGet("/watchlist", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/monitor/watchlist", ct))
            .WithName("BffMonitorWatchlistGet");

        // 追加（symbol/market/reason。重複・空・未定義 market は後段 400。理由必須はフロント/後段が検証）。
        g.MapPost("/watchlist", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Post, "/monitor/watchlist", ct))
            .WithName("BffMonitorWatchlistPost");

        // 削除（本文に symbol/market/reason。不在削除は後段 400）。DELETE でも本文を後段へ転送する。
        g.MapDelete("/watchlist", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Delete, "/monitor/watchlist", ct))
            .WithName("BffMonitorWatchlistDelete");

        // 変更履歴（新しい順・表示専用）。
        g.MapGet("/watchlist/history", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/monitor/watchlist/history", ct))
            .WithName("BffMonitorWatchlistHistory");

        return app;
    }

    // 後段 MarketMonitorService へ pass-through する。ステータス・本文・Content-Type を透過し、
    // 利用者トークンを伝播する。本文を持つメソッド（POST/PUT/PATCH/DELETE）はリクエスト本文をそのまま
    // 転送する。後段不達は 502。RiskControlsBffEndpoints.ProxyAsync と同一方式（バッファ方式・SSE 不要）。
    private static async Task<IResult> ProxyAsync(
        IHttpClientFactory httpFactory, HttpContext http, HttpMethod method, string path, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(ClientName);
        using var req = new HttpRequestMessage(method, path);

        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        // 本文を持つメソッド（POST/PUT/PATCH・および本サービス固有の本文付き DELETE）はリクエスト本文を
        // そのまま後段へ転送する（IADR-0072 決定3。DELETE /monitor/watchlist は銘柄・理由を本文で受ける）。
        if (HttpMethods.IsPost(method.Method) || HttpMethods.IsPut(method.Method)
            || HttpMethods.IsPatch(method.Method) || HttpMethods.IsDelete(method.Method))
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

            // 応答本文は ReadAsStringAsync で一括読み込みし Results.Content で透過する（RiskControlsBff と同方式）。
            // 監視銘柄・変更履歴は小さな管理系ペイロードのためバッファ方式で足りる（SSE 不要）。
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
