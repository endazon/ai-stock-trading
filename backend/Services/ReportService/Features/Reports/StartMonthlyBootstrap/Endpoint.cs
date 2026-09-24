using AppSvc = ReportService.Features.Reports.ReportAppService;

namespace ReportService.Features.Reports.StartMonthlyBootstrap;

internal static class StartMonthlyBootstrapEndpoint
{
    // FR-06, FR-07, UC-03, #839, IADR-0071 決定4, IADR-0382: **初回月報ブートストラップの起動**。
    //
    // 🔴 `GET /reports/monthly-bootstrap` はドラフトを**返すだけ**（下見用）であり、保存も提示もしない。
    // そのため承認待ちに並ばず `/report approve` の対象にもならず、**運用開始直後に方針の連鎖の根が作れなかった**
    // （#839 の原因 2。月報は当月の最終営業日 17:00 まで自動生成されない）。本 POST が保存＋提示を行う。
    //
    // 確定は**しない**（ADR-0003・利用者のみ）。提示（PendingApproval）で止め、以後は Discord の
    // `/report show <periodKey>` / `/report approve <periodKey>` がそのまま使える。
    //
    // 応答: 201＝作成して提示した（Location と版番号つき）／409＝不要（確定済み月報がある）・当月の行が既にある。
    public static void MapStartMonthlyBootstrap(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/monthly-bootstrap", async (
            AppSvc svc, IConfiguration cfg, HttpContext http, CancellationToken cancellationToken) =>
        {
            var watchlist = cfg.GetSection("Reports:Bootstrap:Watchlist").Get<string[]>() ?? [];
            var assumptionsVersion = int.TryParse(cfg["Reports:Bootstrap:AssumptionsVersion"], out var v) && v > 0 ? v : 1;

            var result = await svc
                .StartMonthlyBootstrapAsync(
                    watchlist, assumptionsVersion, ReportEndpoints.ActorOf(http), cancellationToken)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                MonthlyBootstrapOutcome.NotNeeded => Results.Conflict(new
                {
                    error = "確定済みの月報が既にあります。初回ブートストラップは不要です。",
                }),
                MonthlyBootstrapOutcome.PeriodOccupied => Results.Conflict(new
                {
                    error = "当月の月報は既に存在します。既存のドラフトを上書きしません。",
                }),
                // 🔴 提示・通知の失敗を成功に見せない（利用者が「承認待ちに並んでいない」ことに気付ける）。
                _ => Results.Created($"/reports/{result.Report!.PeriodKey}", new
                {
                    periodKey = result.Report.PeriodKey,
                    version = result.Version,
                    presented = result.Presented,
                    notificationFailed = result.NotificationFailed,
                }),
            };
        });
}
