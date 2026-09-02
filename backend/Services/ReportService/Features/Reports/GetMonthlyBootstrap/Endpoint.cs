using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.GetMonthlyBootstrap;

internal static class GetMonthlyBootstrapEndpoint
{
    // FR-06, UC-03, INDEX 決定事項16, IADR-0071 決定4: 初回月報ブートストラップ（初期監視銘柄の選定ドラフト）。
    // 確定済み月報が既にあれば 404（不要）、無ければ当月のブートストラップ月報ドラフトを返す。生成のみ・永続化しない。
    // 初期監視銘柄は構成 Reports:Bootstrap:Watchlist（未設定なら空＝未選定）。/{periodKey} より優先される（リテラル一致）。
    public static void MapGetMonthlyBootstrap(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/monthly-bootstrap", (AppSvc svc, IConfiguration cfg) =>
        {
            var watchlist = cfg.GetSection("Reports:Bootstrap:Watchlist").Get<string[]>() ?? [];
            var assumptionsVersion = int.TryParse(cfg["Reports:Bootstrap:AssumptionsVersion"], out var v) && v > 0 ? v : 1;
            var draft = svc.BuildMonthlyBootstrap(watchlist, assumptionsVersion);
            return draft is null ? Results.NotFound() : Results.Ok(draft);
        });
}
