using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AuditService.Features.AuditEvents.GetAuditEventsByType;

// FR-06, FR-11, #381, IADR-0199 決定2: 種別 × 期間の照会（日報の為替欄が引く）。
//
// 🔴 **認可は OwnerOrService** —— ReportService からの s2s 照会である（IADR-0051 と同じ形）。
// **同グループの他 2 本は OwnerOnly のままにする。必要な 1 本だけを開ける。**
//
// 🔴 **件数の上限を置かない。** 期間の集計に使うため、上限で切ると
// **取りこぼしたことが赤くならない**（上の /events とは用途が違う）。
internal static class GetAuditEventsByTypeEndpoint
{
    public static void MapGetAuditEventsByType(this IEndpointRouteBuilder g) =>
        g.MapGet("/events/by-type", (
                IAuditEventStore store,
                DateTimeOffset from,
                DateTimeOffset to,
                string types) =>
            {
                // 空・空白のみの要素は落とす（`types=,,` を「全件」と解釈しない）。
                var wanted = types
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();

                return wanted.Length == 0
                    ? Results.BadRequest(new { error = "types は 1 つ以上の監査イベント種別が必要です。" })
                    // 半開区間 [from, to)。終端を閉じるとその日の最後の 1 秒が落ちる。
                    : from >= to
                        ? Results.BadRequest(new { error = "from は to より前である必要があります（半開区間）。" })
                        : Results.Ok(store.GetByTypesInPeriod(wanted, from, to));
            })
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);
}
