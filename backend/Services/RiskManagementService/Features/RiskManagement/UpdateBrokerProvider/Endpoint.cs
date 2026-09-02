using AiStockTrading.Shared.Contracts.Trading;
using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement.UpdateBrokerProvider;

// ---- 発注先（FR-20, FR-12, FR-13, SC-02, INDEX 決定 46, #334, IADR-0140/0141）----
// 運用段階とは独立した軸であり、変更操作を持つ画面は SC-02 だけである（SC-03 は参照専用）。
// **実弾（moomoo REAL）への切替は「OK」1 押しで通してはならない**（FR-20 (1)）。同意フラグと
// 「REAL」の文字入力の両方を要求し、欠ければ 400 を返して設定を変更しない。画面だけの統制は
// API 直叩きで消えるため、サーバ側にも同じ関門を置く（IADR-0141 決定1）。
internal static class UpdateBrokerProviderEndpoint
{
    public static void MapUpdateBrokerProvider(this IEndpointRouteBuilder owner) =>
        owner.MapPut("/settings/broker-provider",
            (BrokerProviderUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            // 値域検証: provider の省略（null）は 400。非 nullable enum で受けると本文省略時に既定値 0
            // （＝内蔵 paper）へ暗黙束縛され、「送っていない値へ黙って切り替わる」経路になる
            // （段階遷移エンドポイントの TargetStage と同じ扱い）。
            if (req.Provider is not { } target)
            {
                return Results.BadRequest(new
                {
                    error = "provider は発注先（0=内蔵 paper / 1=moomoo REAL / 2=moomoo SIMULATE）を指定してください。",
                });
            }

            var assessment = svc.UpdateBrokerProvider(
                new BrokerProviderChangeRequest(
                    target,
                    req.Reason ?? string.Empty,
                    req.AcknowledgedLiveTrading,
                    req.Acknowledgement),
                RiskControlEndpoints.ActorOf(http));

            if (!assessment.Accepted)
            {
                return Results.BadRequest(new
                {
                    error = "発注先の変更を受理できません。",
                    details = assessment.Rejections.Select(DescribeBrokerProviderRejection).ToArray(),
                });
            }

            // 受理時は更新後の設定と、段階との組み合わせに関する警告（拒否ではない）を返す。
            return Results.Ok(new { settings = svc.GetCurrent(), skipsStageGate = assessment.SkipsStageGate });
        });

    // FR-20, #334, IADR-0141: 発注先の変更を受理しない理由を利用者向け文言に写す。
    // **何が足りないかを具体的に返す**——「不正な要求です」だけでは、確認操作を求められていること自体が伝わらず、
    // 利用者は統制を回避する方向（API を直接叩く等）へ動機づけられる。
    private static string DescribeBrokerProviderRejection(BrokerProviderChangeRejection rejection) => rejection switch
    {
        BrokerProviderChangeRejection.ReasonRequired =>
            "変更理由は 1 文字以上を指定してください（監査のため必須）。",
        BrokerProviderChangeRejection.LiveAcknowledgementMissing =>
            "実弾（moomoo REAL）への切替には、実資金で執行されることへの同意が必要です。",
        BrokerProviderChangeRejection.LivePhraseMismatch =>
            $"実弾（moomoo REAL）への切替には「{BrokerProviderChange.LiveAcknowledgementPhrase}」の入力が必要です。",
        BrokerProviderChangeRejection.UnknownProvider =>
            "provider は 0=内蔵 paper / 1=moomoo REAL / 2=moomoo SIMULATE のいずれかを指定してください。",
        _ => "発注先の変更を受理できません。",
    };
}

// FR-20, FR-13, SC-02, #334: 発注先の変更要求。
// Provider は nullable（省略・範囲外をエンドポイントで 400 に弾く。既定値 0 への暗黙束縛を防ぐ）。
// AcknowledgedLiveTrading / Acknowledgement は**実弾（moomoo REAL）への切替でのみ**必須である
// （計画: チェックボックスの同意と「REAL」の文字入力の両方。IADR-0141 決定1）。
internal sealed record BrokerProviderUpdateRequest(
    BrokerProvider? Provider,
    string? Reason,
    bool AcknowledgedLiveTrading = false,
    string? Acknowledgement = null);
