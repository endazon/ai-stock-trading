namespace RiskManagementService.Features.RiskManagement.GetRiskStatus;

// ---- 稼働状態の集約照会（FR-10, UC-07, ADR-0009）: 表示専用。Discord `/status` が参照する ----
// 3 統制の状態・優先順位・段階・当日損益・上限使用率・ポジションを 1 回で返す（設定変更は含まない）。
// 認可は OwnerOnly とする（読み取り系だが sizing-context / open-positions の OwnerOrService とは分ける）。
// 理由: 当日損益・ポジション等の機微情報を束ねた利用者向けサマリであり、サービス（trading-service）に開く
// 用途が無いため。最小権限（IADR-0051）に従い、必要のない読み取り権限をサービスへ与えない。
internal static class GetRiskStatusEndpoint
{
    public static void MapGetRiskStatus(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/status", (RiskStatusService svc) => Results.Ok(svc.Build()));
}
