using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;
using ReportService.Common.Exceptions;
using ReportService.Domain;
using ReportService.Features.Reports.ConfirmReport;
using ReportService.Features.Reports.DraftReport;
using ReportService.Features.Reports.GetConfirmedDailyPolicy;
using ReportService.Features.Reports.GetMonthlyBootstrap;
using ReportService.Features.Reports.GetReport;
using ReportService.Features.Reports.GetReportReview;
using ReportService.Features.Reports.ListReports;
using ReportService.Features.Reports.PresentReport;
using ReportService.Features.Reports.RequestReportChanges;
using ReportService.Features.Reports.SummarizePnl;
using ReportService.Features.Reports.UpsertReportDraft;

namespace ReportService.Features.Reports;

// FR-06/07, UC-03〜05, ADR-0003: 報告書のドラフト管理・確定・照会エンドポイント。確定は OwnerOnly（利用者のみ・Keycloak
// trading-owner）。生成AI・自動処理は確定できない（ADR-0003）。確定の遷移時に ReportConfirmed を発行する。
//
// NFR, platform ADR-0068 決定1, IADR-0289 決定1: **本ファイルは「登録表」である。** `MapGroup` ／ タグ ／
// グループ単位の認可・フィルタ ／ `Program.cs` から呼ぶメソッド名（MapReportEndpoints）はここに残す ——
// これらは集約の全操作が使うものであり、特定の 1 操作に属さない。**個々の操作の処理は 3 段目
// （`<操作>/Endpoint.cs`）にある。** 登録の順序も動かさない（ルート登録順・タグ付け・フィルタ適用順を変えないため）。
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

        read.MapGetConfirmedDailyPolicy();

        // ---- 利用者のみ（ADR-0003・OwnerOnly）: 一覧・集計・ドラフト・確定。サービスには許可しない ----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        owner.MapListReports();
        owner.MapGetMonthlyBootstrap();
        owner.MapSummarizePnl();
        owner.MapDraftReport();
        owner.MapGetReport();
        owner.MapUpsertReportDraft();

        // FR-07, UC-03〜05, IADR-0042/0071 決定5: 対話的確定のレビュー操作（提示・差し戻し）。ReportReviewStateMachine を駆動し
        // レビュー局面（ReviewState）を永続化する。改訂は既存 PUT（新ドラフト＝版+1）、承認は既存 confirm が担う。
        // #15 Discord Bot はこれらの HTTP を呼んで提示→差し戻し／承認を駆動する。
        owner.MapGetReportReview();
        owner.MapPresentReport();
        owner.MapRequestReportChanges();

        owner.MapConfirmReport();

        return app;
    }

    // NFR, IADR-0289 決定3: 書き込み系の複数操作（present / request-changes / confirm）が使うため 2 段目に残す。
    internal static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";

    // FR-07, IADR-0042/0071 決定5: レビュー決定を HTTP へ写像する。対象なし=404、受理=200（局面）、
    // 版不一致・不正遷移・確定済み変更=409。actor は認証から入るため ActorRequired は通常起きないが安全側で 400。
    // NFR, IADR-0289 決定3: present / request-changes の 2 操作が使うため 2 段目に残す。
    internal static IResult ReviewResult(ReviewDecision? decision) => decision switch
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
