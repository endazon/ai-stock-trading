namespace RiskManagementService.Features.RiskManagement.GetInFlightOrders;

// NFR, #1502, IADR-0600: 承認済みで終端していない注文（新規建て・決済）。live の E2E の取引影響層の門が BFF 経由で読む。
// 🔴 利用者だけ（OwnerOnly）の読み取り。サービスの呼び手は無い（判断・統制は既存の口を使う）。
internal static class GetInFlightOrdersEndpoint
{
    public static void MapGetInFlightOrders(this IEndpointRouteBuilder owner) =>
        owner.MapGet("/in-flight-orders", (InFlightOrdersService svc) => Results.Ok(svc.Build()));
}
