using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateRiskLimits;

// FR-10, SC-02, UC-06, #362, IADR-0151 決定2: **リスク上限の値域はここで実効させる。**
//
// 本検査の導入前、当エンドポイントには値域検証が一切無く（`UpdateLimits` は actor / reason の空検査のみ）、
// `MaxOrderAmountRatio = 35000`（equity の 35,000 倍）をそのまま受理していた。SC-02 の保存が 400 に
// なっていたのは値が危険だからではなく、**フロントが送るキー名が required プロパティを満たさなかった**
// からにすぎない（#389 が意図的に据え置いた状態）。#362 でキー名を是正した以上、その偶然の防壁は消える。
//
// **画面（SC-02）にも同じ表があるが、実効はここである** —— 画面だけの統制は API 直叩きで消える
// （IADR-0141 決定1 と同じ判断）。規則の単一情報源は `RiskLimitBounds` であり、ここは 400 の details を
// 組み立てるために全件を受け取る（1 件ずつ直させると保存を何度も試させることになる）。
internal static class UpdateRiskLimitsEndpoint
{
    public static void MapUpdateRiskLimits(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/limits", (LimitsUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            var violations = RiskLimitBounds.Validate(req.Limits);
            if (violations.Count > 0)
            {
                // 拒否時は設定を変更せず履歴も残さない（`UpdateLimits` を呼ばない）。
                return Results.BadRequest(new
                {
                    error = "リスク上限の値が設定可能な範囲を外れています。",
                    details = violations,
                });
            }

            svc.UpdateLimits(req.Limits, RiskControlEndpoints.ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });
}

// 上限変更の要求。RiskLimitSettings は具象プロパティのレコードで標準の逆直列化が可能。
public sealed record LimitsUpdateRequest(RiskLimitSettings Limits, string Reason);
