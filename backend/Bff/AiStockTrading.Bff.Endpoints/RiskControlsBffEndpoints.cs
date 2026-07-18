using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Net.Http;

namespace AiStockTrading.Bff.Endpoints;

// Issue #287, FR-14, IADR-0071: AST リスク設定（SC-02）・統制状態参照（SC-03）の BFF 集約。
// RiskManagementService（/risk-controls/*・OwnerOnly）へ pass-through プロキシする。
//
// 認可は後段（OwnerOnly）が強制する。本 BFF は認証（匿名は 401）と利用者トークンの伝播のみを担い、
// 後段のステータス（200/400/403/404/409 等）と本文・Content-Type をそのまま透過する。
// DTO には結合しない（platform → 可変ユニット参照は禁止・IADR-0057。よって型付けせず素通し）。
// 後段不達は 502 へ縮退する（fail-safe）。フロントの存在秘匿（RequireRole→NotFound）はサーバ 401/403 の
// 表示側バックストップ（IADR-0009/0035）。#285 の AssumptionsBffEndpoints と同型。
//
// 登録経路は SC-02/03 が実消費する 6 本のみ（IADR-0071 決定2。kill-switch・pause・sizing-context 等は
// フロントが叩かないため登録しない＝起こり得ない経路への防御的追加を避ける）。
public static class RiskControlsBffEndpoints
{
    // 後段の名前付き HTTP クライアント（BaseAddress は Program.cs で Services:RiskManagementService から設定）。
    internal const string ClientName = "RiskManagementService";

    public static IEndpointRouteBuilder MapRiskControlsBffEndpoints(this IEndpointRouteBuilder app)
    {
        // グループは認証必須（匿名は 401）。owner 判定は後段（OwnerOnly）に委ねる。
        var g = app.MapGroup("/bff/risk-controls")
            .WithTags("RiskControls BFF")
            .RequireAuthorization();

        // SC-02 リスク設定: 現在値・変更履歴・上限変更・ガード変更。いずれも後段 OwnerOnly。
        g.MapGet("/settings", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/risk-controls/settings", ct))
            .WithName("BffRiskControlsSettingsGet");

        g.MapGet("/settings/history", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/risk-controls/settings/history", ct))
            .WithName("BffRiskControlsSettingsHistory");

        // 上限変更（理由必須・楽観排他は後段が検証。400/409 はそのまま透過）。
        g.MapPut("/settings/limits", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Put, "/risk-controls/settings/limits", ct))
            .WithName("BffRiskControlsSettingsLimitsPut");

        // ガード変更（AST IADR-0086。危険な緩和の確認は後段/フロントが担う。BFF は素通し）。
        g.MapPut("/settings/guard", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Put, "/risk-controls/settings/guard", ct))
            .WithName("BffRiskControlsSettingsGuardPut");

        // SC-03 統制状態参照（表示専用）: 稼働状態の集約・段階ゲートの現況。いずれも後段 OwnerOnly。
        g.MapGet("/status", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/risk-controls/status", ct))
            .WithName("BffRiskControlsStatus");

        g.MapGet("/stage-gate", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
            ProxyAsync(httpFactory, http, HttpMethod.Get, "/risk-controls/stage-gate", ct))
            .WithName("BffRiskControlsStageGate");

        return app;
    }

    // 後段 RiskManagementService へ pass-through する。ステータス・本文・Content-Type を透過し、
    // 利用者トークンを伝播する。PUT はリクエスト本文をそのまま転送する。後段不達は 502。
    // AssumptionsBffEndpoints.ProxyAsync と同一方式（バッファ方式・SSE 不要）。
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

            // 応答本文は ReadAsStringAsync で一括読み込みし Results.Content で透過する（AssumptionsBff と同方式）。
            // リスク設定・統制状態は小さな管理系ペイロードのためバッファ方式で足りる（SSE 不要）。
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
