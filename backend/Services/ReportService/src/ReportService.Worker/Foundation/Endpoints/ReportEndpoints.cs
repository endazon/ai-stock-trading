using AiStockTrading.Report.Application;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Report.Worker.Foundation.Adapters;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AppSvc = AiStockTrading.Report.Application.Services.ReportService;

namespace AiStockTrading.Report.Worker.Foundation.Endpoints;

// FR-06/07, UC-03〜05, ADR-0007: 報告書のドラフト管理・確定・照会エンドポイント。確定は OwnerOnly（利用者のみ・Keycloak
// trading-owner）。生成AI・自動処理は確定できない（ADR-0003/0007）。確定の遷移時に ReportConfirmed を発行する。
internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        // 例外→HTTP マッピング（読み書き共通）: 検証失敗は 400、確定済み変更・楽観排他競合は 409。
        var g = app.MapGroup("/reports")
            .WithTags("Reports")
            .AddEndpointFilter(async (ctx, next) =>
            {
                try
                {
                    return await next(ctx);
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
                catch (ReportConcurrencyException e)
                {
                    return Results.Conflict(new { error = e.Message });
                }
                catch (InvalidOperationException e)
                {
                    return Results.Conflict(new { error = e.Message });
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new { error = "報告書が他の更新と競合しました。最新を取得して再試行してください。" });
                }
            });

        // ---- 読み取り系: 利用者またはサービス（IADR-0051・OwnerOrService） ----
        // 確定済み日報の方針（取引判断#11 の実データ源）を s2s（trading-service）でも同期照会できるよう分離する。
        var read = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        // 確定済み日報の方針。未確定なら 404。/{periodKey} より優先される（リテラル一致）。
        read.MapGet("/daily-policy", (AppSvc svc) =>
        {
            var policy = svc.GetConfirmedDailyPolicy();
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        // ---- 利用者のみ（ADR-0007・OwnerOnly）: 一覧・集計・ドラフト・確定。サービスには許可しない ----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        owner.MapGet("", (AppSvc svc) => Results.Ok(svc.List()));

        // FR-06, UC-03, INDEX 決定事項16, IADR-0071 決定4: 初回月報ブートストラップ（初期監視銘柄の選定ドラフト）。
        // 確定済み月報が既にあれば 404（不要）、無ければ当月のブートストラップ月報ドラフトを返す。生成のみ・永続化しない。
        // 初期監視銘柄は構成 Reports:Bootstrap:Watchlist（未設定なら空＝未選定）。/{periodKey} より優先される（リテラル一致）。
        owner.MapGet("/monthly-bootstrap", (AppSvc svc, IConfiguration cfg) =>
        {
            var watchlist = cfg.GetSection("Reports:Bootstrap:Watchlist").Get<string[]>() ?? [];
            var assumptionsVersion = int.TryParse(cfg["Reports:Bootstrap:AssumptionsVersion"], out var v) && v > 0 ? v : 1;
            var draft = svc.BuildMonthlyBootstrap(watchlist, assumptionsVersion);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        });

        // FR-16, IADR-0025: 損益集計（数値はコードで集計）。約定列＋任意の現在値から実現損益・費用・税・評価損益を返す。
        // 前提条件は暫定で既定値（#19 のバージョン付き取得・#63 台帳連携は #22 後続）。
        owner.MapPost("/pnl-summary", (PnlSummaryRequest req) =>
        {
            var summary = PnlAggregator.Aggregate(
                req.Fills ?? [], TradingAssumptionsDefaults.Create(), req.CurrentPrices);
            return Results.Ok(summary);
        });

        // FR-06/16, IADR-0032: 報告書ドラフト生成（日報/週報/月報・数値はコード集計・散文は LLM ドラフト）。生成のみで永続化しない。
        // 前提条件は暫定既定値（#19 のバージョン付き取得・#63 台帳連携は #22 後続）。
        owner.MapPost("/{periodKey}/draft", async (string periodKey, DraftReportRequest req, ReportDraftService svc, CancellationToken ct) =>
        {
            // FR-07, IADR-0117 決定5: 上位方針の本文は任意（省略時 null＝上位なし）。自動生成が主経路であり、
            // 手動経路は呼び出し側が文脈を持つ場合のための穴に留める（既存の呼び出しは非破壊）。
            var draft = await svc.BuildDraftAsync(new DraftRequest(
                req.Kind, periodKey, req.Date, req.Markets, req.AssumptionsVersion, req.BasedOn,
                req.PolicySummary ?? string.Empty, req.Fills, req.CurrentPrices,
                ParentPolicySummary: req.ParentPolicySummary), ct);
            return Results.Ok(new { periodKey, markdown = draft.Markdown, pnl = draft.Pnl });
        });

        owner.MapGet("/{periodKey}", (string periodKey, AppSvc svc) =>
        {
            var report = svc.Get(periodKey);
            return report is null ? Results.NotFound() : Results.Ok(report);
        });

        // ドラフトの作成/更新（利用者のみ・楽観排他）。
        owner.MapPut("/{periodKey}", (string periodKey, UpsertReportRequest req, AppSvc svc) =>
        {
            var report = new TradingReport
            {
                PeriodKey = periodKey,
                Kind = req.Kind,
                PeriodStart = req.PeriodStart,
                BasedOn = req.BasedOn,
                AssumptionsVersion = req.AssumptionsVersion,
                PolicySummary = req.PolicySummary,
            };
            var version = svc.UpsertDraft(report, req.ExpectedVersion);
            return Results.Ok(new { periodKey, version });
        });

        // FR-07, UC-03〜05, IADR-0042/0071 決定5: 対話的確定のレビュー操作（提示・差し戻し）。ReportReviewStateMachine を駆動し
        // レビュー局面（ReviewState）を永続化する。改訂は既存 PUT（新ドラフト＝版+1）、承認は既存 confirm が担う。
        // #15 Discord Bot はこれらの HTTP を呼んで提示→差し戻し／承認を駆動する。

        // 現在のレビュー局面（状態＋版番号）。Bot/UI が次操作の期待版を得るために照会する。
        owner.MapGet("/{periodKey}/review", (string periodKey, AppSvc svc) =>
        {
            var review = svc.GetReview(periodKey);
            return review is null ? Results.NotFound() : Results.Ok(review);
        });

        // 提示（Drafting/ChangesRequested→PendingApproval）。内容不変のため版番号は変わらない。
        owner.MapPost("/{periodKey}/present", (string periodKey, ReviewCommandRequest req, AppSvc svc, HttpContext http) =>
            ReviewResult(svc.ApplyReview(periodKey, ReviewAction.Present, req.ExpectedVersion, ActorOf(http))));

        // 差し戻し（PendingApproval→ChangesRequested）。修正指示を受けて改訂を促す。内容不変のため版番号は変わらない。
        owner.MapPost("/{periodKey}/request-changes", (string periodKey, ReviewCommandRequest req, AppSvc svc, HttpContext http) =>
            ReviewResult(svc.ApplyReview(periodKey, ReviewAction.RequestChanges, req.ExpectedVersion, ActorOf(http))));

        // 確定（Draft→Confirmed・利用者のみ・版番号付き冪等）。遷移時のみ ReportConfirmed を発行し、確定報告書を KB へ保存する。
        // 対象が無ければ 404。KB 保存は best-effort（既定 no-op・fail-safe＝確定を壊さない・FR-08/IADR-0071 決定3）。
        owner.MapPost("/{periodKey}/confirm", async (string periodKey, ConfirmReportRequest req, AppSvc svc,
            IPublishEndpoint bus, IKnowledgeBaseWriter kb, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var actor = ActorOf(http);
            var result = svc.Confirm(periodKey, req.ExpectedVersion, actor);
            if (result is null)
                return Results.NotFound();

            if (result.Transitioned)
            {
                var r = result.Report;
                await bus.Publish(new ReportConfirmed(
                    r.PeriodKey, r.Kind.ToString(), actor, r.AssumptionsVersion, r.ConfirmedAt ?? DateTimeOffset.UtcNow));

                // FR-08, IADR-0069/0071 決定3: 確定報告書を KB へ保存（カタログ登録）。既定 no-op。
                // 保存の失敗・例外は握りつぶし確定を壊さない（KB は best-effort・保存ポート自体も fail-safe）。
                try
                {
                    await kb.SaveAsync(ReportKnowledgeMapper.ToDocument(r), http.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggerFactory.CreateLogger("ReportKnowledgeBase")
                        .LogWarning(ex, "確定報告書 {PeriodKey} の KB 保存に失敗しました（確定は継続）。", r.PeriodKey);
                }
            }

            return Results.Ok(result.Report);
        });

        return app;
    }

    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";

    // FR-07, IADR-0042/0071 決定5: レビュー決定を HTTP へ写像する。対象なし=404、受理=200（局面）、
    // 版不一致・不正遷移・確定済み変更=409。actor は認証から入るため ActorRequired は通常起きないが安全側で 400。
    private static IResult ReviewResult(ReviewDecision? decision) => decision switch
    {
        null => Results.NotFound(),
        { Accepted: true } d => Results.Ok(d.Review),
        { Rejection: ReviewRejectionReason.ActorRequired } => Results.BadRequest(new { error = "操作者が必要です。" }),
        var d => Results.Conflict(new { error = RejectionMessage(d!.Rejection!.Value) }),
    };

    private static string RejectionMessage(ReviewRejectionReason reason) => reason switch
    {
        ReviewRejectionReason.VersionConflict => "版番号が一致しません。最新のドラフトを確認してください。",
        ReviewRejectionReason.AlreadyConfirmed => "確定済みの報告書は変更できません。",
        _ => "現在の状態から許可されない操作です。",
    };
}

// FR-07, IADR-0071 決定5: レビュー操作の要求（版番号付き楽観排他）。
internal sealed record ReviewCommandRequest(int ExpectedVersion);

// ドラフト upsert の要求。TradingReport は具象レコードのため標準の逆直列化が可能。
internal sealed record UpsertReportRequest(
    ReportKind Kind,
    DateOnly PeriodStart,
    string? BasedOn,
    int AssumptionsVersion,
    string PolicySummary,
    int ExpectedVersion);

// 確定の要求（版番号付き冪等）。
internal sealed record ConfirmReportRequest(int ExpectedVersion);

// 損益集計の要求（約定列＋任意の現在値。銘柄→現在値）。
internal sealed record PnlSummaryRequest(List<PeriodTradeFill> Fills, Dictionary<string, decimal>? CurrentPrices);

// 報告書ドラフト生成の要求（数値はコード集計・散文は LLM ドラフト）。Kind で日報/週報/月報を切り替える。
// Fills は集計対象の約定列（#63 台帳連携は #22 後続）。
// FR-07, IADR-0117 決定5: ParentPolicySummary は上位方針（BasedOn が指す報告書）の本文。任意（既定 null）。
internal sealed record DraftReportRequest(
    ReportKind Kind,
    DateOnly Date,
    List<string>? Markets,
    int AssumptionsVersion,
    string? BasedOn,
    string? PolicySummary,
    List<PeriodTradeFill>? Fills,
    Dictionary<string, decimal>? CurrentPrices,
    string? ParentPolicySummary = null);
