using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AuditService.Features.AuditEvents;

// FR-11, UC-07, IADR-0019: 監査台帳の照会エンドポイント。監査は取引履歴＝機微情報のため、すべて OwnerOnly
// （利用者のみ・Keycloak ロール trading-owner）を要求する（RiskControl と同じ認可方針）。
internal static class AuditQueryEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapAuditQueryEndpoints(this IEndpointRouteBuilder app)
    {
        // 🔴 **認可はグループではなくエンドポイント単位に置く**（#381 供給結線・IADR-0199 決定2）。
        // `RequireAuthorization` は**加算**であり、グループへ OwnerOnly を置いたまま個別に
        // OwnerOrService を足すと**両方を満たす必要が生じ、サービスロールが 403 になる**
        //（実測。「開けたつもりで開いていない」形である）。
        var g = app.MapGroup("/audit").WithTags("Audit");

        // 注文単位（相関）の全記録を時系列（昇順）で返す＝「いつ・何を根拠に・何をしたか」を辿る。
        g.MapGet("/events/{correlationId:guid}", (Guid correlationId, IAuditEventStore store) =>
                Results.Ok(store.GetByCorrelation(correlationId)))
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        // 直近の記録（降順）。limit は 1〜500 にクランプ（既定 100）。
        g.MapGet("/events", (IAuditEventStore store, int? limit) =>
                Results.Ok(store.GetRecent(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))))
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        // FR-06, FR-11, #381, IADR-0199 決定2: 種別 × 期間の照会（日報の為替欄が引く）。
        //
        // 🔴 **認可は OwnerOrService** —— ReportService からの s2s 照会である（IADR-0051 と同じ形）。
        // **同グループの他 2 本は OwnerOnly のままにする。必要な 1 本だけを開ける。**
        //
        // 🔴 **件数の上限を置かない。** 期間の集計に使うため、上限で切ると
        // **取りこぼしたことが赤くならない**（上の /events とは用途が違う）。
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

        return app;
    }
}
