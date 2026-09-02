namespace RiskManagementService.Features.RiskManagement.GetShortSellingStatus;

// ---- 空売りの現況（FR-10, UC-06, SC-03, ADR-0016 決定3/7/9/15, #340, IADR-0154）: 表示専用 ----
// 維持率（SC-03 の最上位）・空売り比率・保有建玉の方向・借株料の累計・維持率割れ自動縮小の現況。
// **応答は各指標の供給可否（MetricAvailability）を明示的に宣言する。** 維持率・借株料の累計・
// 発動履歴は供給元が無く、0 や空列で運ぶと画面が「正常な統制」として描いてしまう（#403 と同型の
// fail-open）。供給可否の判定はサーバ側にしか置けない——フロントへ「未供給」と書き込むと、
// 供給元が入った日に画面が嘘をつき続ける。
// 認可は /status と同じ OwnerOnly（機微情報を束ねた利用者向けサマリであり s2s の用途が無い）。
internal static class GetShortSellingStatusEndpoint
{
    public static void MapGetShortSellingStatus(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/short-selling", (ShortSellingStatusService svc) => Results.Ok(svc.Build()));
}
