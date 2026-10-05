using AiStockTrading.Shared.Contracts.Logging;
using ReportService.Features.Reports.ConfirmReport;

namespace ReportService.Features.Reports.RegenerateReport;

internal static class RegenerateReportEndpoint
{
    // FR-06, FR-14, UC-03〜05, 計画 ADR-0052 決定 1〜5, #1156, IADR-0491 決定 1: 未確定の下書きを、その期間の入力で作り直す（方針は保つ・
    // 版を上げて再提示する・**確定はしない**）。OwnerOnly（登録表 ReportEndpoints の owner グループ＝所有者の門）。
    //
    // 作り直した利用者は確定・`/policy` と同じ ConfirmingActorResolver で決める（Discord Bot は owner マップ機密クライアントのトークンで呼び、
    // 本文の onBehalfOf は信頼クライアントに限って採る。IADR-0240 決定11）。
    //
    // 応答: 200＝作り直して保存した／400＝会話キー・代理指定の不正／404＝対象なし／409＝確定済み・期間の不整合・並行更新／
    // 422＝中核の入力の取得に失敗したので断った（回数は消費しない）／429＝本日の上限。**200 以外では下書きを変えていない。**
    public static void MapRegenerateReport(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/{periodKey}/regenerate", (string periodKey, RegenerateReportRequest? req, ReportRegenerationService svc,
            DelegatedActorOptions delegated, ILoggerFactory loggerFactory, HttpContext http) =>
                HandleAsync(periodKey, req ?? new RegenerateReportRequest(), svc, delegated, loggerFactory, http));

    // NFR, IADR-0450: REST と gRPC 面（ReportOwnerWriteGrpcService）が共有する処理（作り直した利用者の解決・状態の写しを 2 箇所に書かない）。
    internal static async Task<IResult> HandleAsync(string periodKey, RegenerateReportRequest req, ReportRegenerationService svc,
        DelegatedActorOptions delegated, ILoggerFactory loggerFactory, HttpContext http)
    {
        var regenerating = ConfirmingActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
        if (regenerating.Rejected)
        {
            loggerFactory.CreateLogger("ReportRegenerationActor").LogWarning(
                "報告書の作り直しの代理される利用者（OnBehalfOf）が値域外のため拒否しました。");
            return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
        }

        if (regenerating.IgnoredOnBehalfOf)
        {
            loggerFactory.CreateLogger("ReportRegenerationActor").LogWarning(
                "報告書の作り直しの OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。操作者={Actor}）。",
                LogSanitizer.Sanitize(regenerating.Actor));
        }

        var result = await svc.RegenerateAsync(periodKey, regenerating.Actor, http.RequestAborted);
        return result.Status switch
        {
            ReportRegenerationStatus.Regenerated => Results.Ok(ReportRegenerationResponse.From(result)),
            ReportRegenerationStatus.InvalidPeriodKey => Results.BadRequest(new { error = result.Message }),
            ReportRegenerationStatus.NotFound => Results.NotFound(new { error = result.Message }),
            ReportRegenerationStatus.DailyLimitReached =>
                Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status429TooManyRequests),
            ReportRegenerationStatus.CoreInputsUnsupplied =>
                Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.Conflict(new { error = result.Message }),
        };
    }
}

// FR-06, 計画 ADR-0052, IADR-0491 決定 1: 作り直しの要求。OnBehalfOf は代理される利用者（Discord Bot が載せる。信頼クライアント以外では無視）。
public sealed record RegenerateReportRequest(string? OnBehalfOf = null);

// FR-06, 計画 ADR-0052 決定 5, IADR-0491 決定 5: 作り直しの応答（200 のときだけ）。文字列はコード定数・会話キー・入力の表示名だけ
// （LLM の出力を含まない。本文は返さない＝ `/report show` と同じく Bot は本文を取りに行かない。IADR-0240 決定4）。
public sealed record ReportRegenerationResponse(
    string PeriodKey,
    int PreviousVersion,
    int Version,
    bool Presented,
    string Message,
    IReadOnlyList<string> UnsuppliedInputs,
    IReadOnlyList<string> NotRestorableInputs)
{
    internal static ReportRegenerationResponse From(ReportRegenerationResult result) =>
        new(result.PeriodKey!, result.PreviousVersion, result.Version, result.Presented, result.Message,
            result.UnsuppliedInputs, result.NotRestorableInputs);
}
