using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AiStockTrading.Bff.Endpoints;

// SC-04, FR-09, FR-11, UC-06（代替フロー「ゲートウェイの有人認証」）, NFR-03, NFR-05, NFR-06,
// ADR-0002 前提条件1, ADR-0024 決定1・2, IADR-0321: OpenD 認証操作（SC-04）の BFF。
// 上流は OpenD 常駐 Pod 内のサイドカー（クラスタ内・#722）。
//
// ── 既存 3 モジュール（Assumptions / RiskControls / Monitor）との意図的な非対称 ──
//
// 🔴 **① 認可を後段へ委ねられない。** 既存モジュールは `RequireAuthorization()` だけを掛け、owner 判定を
//    後段（`OwnerOnly` を持つドメインサービス）へ委ねる。**本モジュールの上流は設計上まったく認証を
//    持たない**（Ingress を持たず Pod 網にしか bind しない）。委譲先が無いため **BFF が唯一の認可点**である。
//    `trading-owner` を持たない利用者へは **404**（403 ではない）を返す＝**存在秘匿**（NFR-06）。
//
// 🔴 **② 本文を素通ししない。** 既存モジュールは要求本文を丸ごと透過するが、本モジュールは
//    **上流へ渡す本文を作り直す**（`OpendAuthUpstream.UpstreamVerifyRequest`）。素通しにすると、
//    画面から来た本文に欄が増えたとき——たとえば `command` ——**それがそのまま上流へ届く**。
//    作り直す形なら、型に無い欄は構造的に落ちる。
//
// 🔴 **③ 応答本文を素通ししない。** 検証コードは**応答にも載せない**（要求と同じ値でも返さない）。
//    上流の本文をそのまま返すと、上流が将来エコーし始めた瞬間に漏れる。**理由の符号だけを返す。**
//
// ── 記録しないこと（NFR-05 の適用範囲を広げた扱い。計画 05_screens SC-04）──
//
// 🔴 **検証コードの値はどこにも残さない。** 監査ログ・送信履歴・Discord 通知・サーバログ・応答の
//    いずれにも値を書かない。残すのは**送信した事実・日時・アクター・コマンド種別・結果**だけである。
//    本 BFF は要求本文をログへ出さず、**例外メッセージも出さない**
//    （`JsonException` のメッセージには本文の断片が入るため）。
//
// ── 送れるコマンド（許可リスト方式。拒否リストではない）──
//
//    `input_phone_verify_code` / `input_pic_verify_code` / `req_phone_verify_code` の 3 つだけ。
//    **利用者はコマンドを選べない**——送るコマンドは**サーバが待機中のプロンプトから決める**。
//    一覧と「送れないもの」は `OpendAuthUpstream` を参照（本ファイルへ二重に書かない）。
public static class OpendAuthBffEndpoints
{
    /// <summary>後段の名前付き HTTP クライアント。基底 URL は構成（<c>OpendAuth:BaseUrl</c>）から解決する。</summary>
    internal const string ClientName = "OpendAuthGateway";

    /// <summary>
    /// 利用者ロール（Keycloak のレルムロール）。基盤の <c>OwnerOnly</c> ポリシーと同一のロール名である。
    /// 定数を持つのは、本ライブラリが ASP.NET Core だけで自己完結する契約のため
    /// （`AiStockTradingAuthPolicies` を参照すると依存が増える）。
    /// </summary>
    internal const string OwnerRole = "trading-owner";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOpendAuthBffEndpoints(this IEndpointRouteBuilder app)
    {
        // グループは認証必須（匿名は 401。既存 BFF と同じ既定であり、ログイン導線を壊さない）。
        var g = app.MapGroup("/bff/opend-auth")
            .WithTags("OpendAuth BFF")
            .RequireAuthorization();

        // 🔴 **認可はグループのフィルタ 1 つで掛ける。** 端点ごとに書くと、次に端点を足した人が落とす
        // ——そして落としても「動く」ので誰も気づかない。ロールが無ければ **404** を返し、
        // **上流を 1 度も呼ばない**（呼ぶと応答時間の差だけで端点の存在が分かる）。
        g.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.User.IsInRole(OwnerRole) ? await next(ctx) : Results.NotFound());

        // SC-04: ゲートウェイの状態（参照）。**未構成・不達でも 200 を返し、供給が無いことを宣言する。**
        // エラーで返さないのは、画面が「取れていない」と「入力待ちではない」を描き分けるために
        // 宣言そのものを要るからである（05_screens「供給可否はサーバ側が宣言する」）。
        g.MapGet("/state", GetStateAsync).WithName("BffOpendAuthState");

        // SC-04: 画像 CAPTCHA。OpenD が返した画像をそのまま中継する（image/png）。不在は 404。
        g.MapGet("/captcha", GetCaptchaAsync).WithName("BffOpendAuthCaptcha");

        // SC-04: 検証コードの投入。**本文はコードだけ**であり、コマンド種別は受け取らない。
        g.MapPost("/code", PostCodeAsync).WithName("BffOpendAuthCode");

        // SC-04: SMS の再送要求（本文なし）。レート制限は上流が課す（暫定 60 秒に 1 回・実測待ち）。
        g.MapPost("/resend", PostResendAsync).WithName("BffOpendAuthResend");

        return app;
    }

    // ---- /state ----

    private static async Task<IResult> GetStateAsync(
        IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct)
    {
        // 配備構成由来の 2 指標は上流の到達性と独立に供給され得る（ADR-0024 決定1 の 2 条件）。
        var deviceTrust = config.GetValue<bool?>(OpendAuthUpstream.DeviceTrustPersistedKey);
        var egressStable = config.GetValue<bool?>(OpendAuthUpstream.EgressStableKey);

        var baseUrl = ResolveBaseUrl(config);
        if (baseUrl is null)
        {
            // 未構成は**供給が無い**。0 や「—」ではなく、そう宣言する（fail-safe）。
            return Results.Ok(OpendAuthUpstream.Unsupplied(
                "ゲートウェイの接続先が構成されていません。", deviceTrust, egressStable));
        }

        try
        {
            var client = httpFactory.CreateClient(ClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + OpendAuthUpstream.StatePath);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return Results.Ok(OpendAuthUpstream.Unsupplied(
                    "ゲートウェイの状態を取得できませんでした。", deviceTrust, egressStable));
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var upstream = JsonSerializer.Deserialize<OpendAuthUpstream.UpstreamState>(body, Json);
            return Results.Ok(OpendAuthUpstream.ToView(upstream, deviceTrust, egressStable));
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, ct))
        {
            // 🔴 **例外の内容を応答へ載せない。** 上流の URL・本文の断片が混じり得る。
            return Results.Ok(OpendAuthUpstream.Unsupplied(
                "ゲートウェイへ到達できませんでした。", deviceTrust, egressStable));
        }
    }

    // ---- /captcha ----

    private static async Task<IResult> GetCaptchaAsync(
        IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl(config);
        if (baseUrl is null) return Results.NotFound();

        try
        {
            var client = httpFactory.CreateClient(ClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + OpendAuthUpstream.CaptchaPath);
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return Results.NotFound();

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            // 使い捨ての一枚である。中間・ブラウザのどちらにも残さない。
            return Results.File(bytes, "image/png");
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, ct))
        {
            return Results.NotFound();
        }
    }

    // ---- /code ----

    /// <summary>
    /// 検証コードを投入する。
    /// <para>
    /// 順序が効く: <b>本文の解釈 → 待機中プロンプトの取得（＝コマンドの決定） → 上流へ投入</b>。
    /// 🔴 <b>コマンドを決めるのは「いま上流が何を待っているか」だけである。</b> クライアントの本文は
    /// 一切影響しない——影響し得る欄が <see cref="OpendAuthUpstream.UpstreamVerifyRequest"/> に無い。
    /// </para>
    /// <para>
    /// 待機していなければ <b>409</b> を返し、**上流へ 1 バイトも送らない**。画面側の無効化に頼らない
    /// （計画 05_screens SC-04「サーバ側でも拒否する。画面の無効化だけに頼らない」）。
    /// </para>
    /// </summary>
    private static async Task<IResult> PostCodeAsync(
        IHttpClientFactory httpFactory, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl(config);
        if (baseUrl is null) return GatewayUnavailable();

        // 🔴 本文の読み取りで例外を握るとき、**例外メッセージを一切外へ出さない**
        // （`JsonException` のメッセージには本文の断片＝検証コードが入る）。
        string? code;
        try
        {
            var request = await JsonSerializer.DeserializeAsync<CodeSubmission>(http.Request.Body, Json, ct);
            code = request?.Code;
        }
        catch (JsonException)
        {
            return Rejected();
        }

        if (string.IsNullOrWhiteSpace(code)) return Rejected();

        try
        {
            var client = httpFactory.CreateClient(ClientName);

            // ── コマンドの決定（サーバ側）──
            // いま待たれているプロンプトを読み、そこからコマンドを決める。
            var prompt = await ReadPromptAsync(client, baseUrl, ct);
            if (OpendAuthUpstream.CommandForPrompt(prompt) is null)
            {
                // 待機していない・種別が読めない → **送らない**。
                return NotWaiting();
            }

            // ── 投入 ──
            // 本文は**作り直す**（画面から来た本文をそのまま流さない）。載るのはコードだけである。
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + OpendAuthUpstream.VerifyPath)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new OpendAuthUpstream.UpstreamVerifyRequest(code), Json),
                    Encoding.UTF8,
                    "application/json"),
            };
            using var resp = await client.SendAsync(req, ct);
            return MapSubmissionResult(resp.StatusCode);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, ct))
        {
            return GatewayUnavailable();
        }
    }

    // ---- /resend ----

    private static async Task<IResult> PostResendAsync(
        IHttpClientFactory httpFactory, IConfiguration config, CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl(config);
        if (baseUrl is null) return GatewayUnavailable();

        try
        {
            var client = httpFactory.CreateClient(ClientName);
            // 本文なし。送るコマンドは `req_phone_verify_code` の 1 つに固定であり、選択肢が無い。
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + OpendAuthUpstream.ResendPath);
            using var resp = await client.SendAsync(req, ct);
            return MapSubmissionResult(resp.StatusCode);
        }
        catch (Exception ex) when (IsUpstreamFailure(ex, ct))
        {
            return GatewayUnavailable();
        }
    }

    // ---- 補助 ----

    /// <summary>
    /// 画面から受ける本文。<b>コードだけ</b>である。
    /// 🔴 <c>command</c> / <c>kind</c> に相当する欄を**足さないこと**。足した瞬間に
    /// 「利用者はコマンドを選べない」という本画面の中核の統制が壊れる。
    /// </summary>
    private sealed record CodeSubmission(string? Code);

    /// <summary>
    /// 構成から上流の基底 URL を解決する。**空・空白は「未構成」**として <c>null</c> を返す
    /// （`KnowledgeBase__Search__BaseUrl` の空＝no-op と同じ fail-safe な既定）。
    /// </summary>
    private static string? ResolveBaseUrl(IConfiguration config)
    {
        var raw = config[OpendAuthUpstream.BaseUrlKey];
        return string.IsNullOrWhiteSpace(raw) ? null : raw.TrimEnd('/');
    }

    /// <summary>いま上流が待っているプロンプト種別。読めなければ <c>null</c>。</summary>
    private static async Task<string?> ReadPromptAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + OpendAuthUpstream.StatePath);
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var body = await resp.Content.ReadAsStringAsync(ct);
        OpendAuthUpstream.UpstreamState? state;
        try
        {
            state = JsonSerializer.Deserialize<OpendAuthUpstream.UpstreamState>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        // 写像を通してから読む（`status` と `prompt` の整合はここで一度だけ判定する）。
        var view = OpendAuthUpstream.ToView(state, null, null);
        return view.PromptAvailability == OpendAuthUpstream.Available ? view.Prompt : null;
    }

    /// <summary>
    /// 上流の投入結果を画面向けの応答へ写す。
    /// 🔴 <b>上流の本文は捨てる。</b> 返すのは状態と機械可読な理由の符号だけであり、
    /// **利用者の入力に由来する文字列を 1 つも含まない**（検証コードが応答へ回り込む経路を塞ぐ）。
    /// </summary>
    private static IResult MapSubmissionResult(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Accepted => Results.NoContent(),
        HttpStatusCode.Conflict => NotWaiting(),
        HttpStatusCode.BadRequest => Rejected(),
        _ => GatewayUnavailable(),
    };

    /// <summary>いま入力を待っていない（＝送るコマンドが決まらない）。</summary>
    private static IResult NotWaiting() =>
        Results.Json(new { reason = "not_waiting" }, statusCode: StatusCodes.Status409Conflict);

    /// <summary>上流が受け付けなかった。**何が違ったかは返さない**（入力を反射しない）。</summary>
    private static IResult Rejected() =>
        Results.Json(new { reason = "rejected" }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>上流が未構成・不達・その他の失敗。**供給が無い**側へ倒す。</summary>
    private static IResult GatewayUnavailable() =>
        Results.Json(new { reason = "gateway_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>上流の不達・タイムアウト（利用者のキャンセルは除く）。</summary>
    private static bool IsUpstreamFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested;
}
