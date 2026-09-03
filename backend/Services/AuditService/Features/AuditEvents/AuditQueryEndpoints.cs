using AuditService.Features.AuditEvents.GetAuditEventsByCorrelation;
using AuditService.Features.AuditEvents.GetAuditEventsByType;
using AuditService.Features.AuditEvents.GetRecentAuditEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace AuditService.Features.AuditEvents;

// FR-11, UC-07, IADR-0019: 監査台帳の照会エンドポイント。監査は取引履歴＝機微情報のため、すべて OwnerOnly
// （利用者のみ・Keycloak ロール trading-owner）を要求する（RiskControl と同じ認可方針）。
//
// NFR, platform ADR-0068 決定1: **本ファイルは「登録表」である。** `MapGroup` ／ タグ ／
// `Program.cs` から呼ぶメソッド名（MapAuditQueryEndpoints）はここに残す。**個々の操作の処理は
// 3 段目（`<操作>/Endpoint.cs`）にある。** 登録の順序も動かさない。
internal static class AuditQueryEndpoints
{
    public static IEndpointRouteBuilder MapAuditQueryEndpoints(this IEndpointRouteBuilder app)
    {
        // 🔴 **認可はグループではなくエンドポイント単位に置く**（#381 供給結線・IADR-0199 決定2）。
        // `RequireAuthorization` は**加算**であり、グループへ OwnerOnly を置いたまま個別に
        // OwnerOrService を足すと**両方を満たす必要が生じ、サービスロールが 403 になる**
        //（実測。「開けたつもりで開いていない」形である）。
        var g = app.MapGroup("/audit").WithTags("Audit");

        g.MapGetAuditEventsByCorrelation();
        g.MapGetRecentAuditEvents();
        g.MapGetAuditEventsByType();

        return app;
    }
}
