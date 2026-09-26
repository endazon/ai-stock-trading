using ReportService.Features.Reports.ConfirmReport;

namespace ReportService.Features.Reports.ReingestKnowledgeBase;

internal static class ReingestKnowledgeBaseEndpoint
{
    // 誰の代理も信じない（この操作は代理の窓口〔Discord Bot〕を持たない）。操作者はトークンの主体だけで決める。
    private static readonly IReadOnlySet<string> NoTrustedClients = new HashSet<string>(StringComparer.Ordinal);

    // FR-08, FR-11, #1028, IADR-0436: 確定済みの報告書を KB へ入れ直す（所有者専用。登録表 ReportEndpoints の owner グループ）。
    // 基盤の切替で消えた写しの復旧と、本文なしで入った写し（#565）の修復に使う。冪等（2 回目は KB を増やさない）。
    //
    // 応答: 200＝実行した（個別の失敗・不明を含み得る。items を見る）／400＝範囲の指定の不正／409＝実行中／
    // 503＝KB が構成されていない／502＝KB の文書一覧を引けなかった。**503・502 では 1 件も書いていない。**
    // 200・503・502 は監査台帳へ ReportKnowledgeReingested を残す（400・409 は何もしていないので残さない）。
    public static void MapReingestKnowledgeBase(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/knowledge-base/reingest", async (ReportKnowledgeReingestRequest req,
            ReportKnowledgeReingestService svc, HttpContext http) =>
        {
            var (scope, error) = ReportKnowledgeReingestScope.Parse(req.All, req.FromPeriodKey, req.ToPeriodKey);
            if (scope is null)
                return Results.BadRequest(new { error });

            var actor = ConfirmingActorResolver.Resolve(http.User, onBehalfOf: null, NoTrustedClients).Actor;
            var run = await svc.RunAsync(scope, req.RefreshExisting == true, actor, http.RequestAborted);

            return run.Result is null
                ? Results.Conflict(new { error = "KB への入れ直しが実行中です。終わってから実行してください。" })
                : Results.Json(run.Result, statusCode: run.StatusCode);
        });
}
