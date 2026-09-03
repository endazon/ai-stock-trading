using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Wolverine;

namespace InformationCollectionService.Features.InformationCollection.ActivateGeneralWebCollection;

// FR-01, FR-11, #336, ADR-0020 決定4: 一般インターネット収集（最終手段）の**発動申請**。
// 4 条件をすべて満たす場合に限り、**次回月報までの暫定措置**として発動を記録する。
//
// 🔴 **認可は OwnerOnly**（run-once の OwnerOrService とは非対称）。ADR-0020 決定4 は
// **利用者の承認**を要件としており、無人サービスが自動で発動してよいものではない。
//
// 🔴 **本エンドポイントは発動条件の判定と記録だけを行い、一般 Web からの取得は実装しない。**
// 承認前に取得経路を作らない（条件が成立していない状態で「使える」ものを置かない）。
internal static class Endpoint
{
    internal static void MapActivateGeneralWebCollection(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/collection/general-web-activation",
            async (GeneralWebActivationRequest request, IMessageBus publish, IClock clock, CancellationToken ct) =>
            {
                var decision = GeneralWebActivationPolicy.Evaluate(request, clock.UtcNow);
                if (!decision.Approved)
                {
                    // **満たしていない条件を必ず返す。** 「だいたい満たしている」を作らない。
                    return Results.BadRequest(new { error = "一般 Web 収集の発動条件を満たしていません。", decision.UnmetConditions });
                }

                await publish.PublishAsync(new GeneralWebCollectionStateChanged(
                    request.Category,
                    Engaged: true,
                    Reason: $"ADR-0020 決定4 の 4 条件を充足（欠測 {request.OutageBusinessDays} 営業日"
                        + $"・提供終了公表={request.ProviderAnnouncedDiscontinuation}"
                        + $"・実害の記録確認={request.HarmConfirmedInReports}"
                        + $"・規約上の自動取得可={request.TermsPermitAutomatedAccess}"
                        + $"・データ分離={request.DataSeparationApplied}"
                        + $"・複数独立ソースの裏取り={request.CorroboratedByIndependentSources}）",
                    decision.ProvisionalUntil,
                    clock.UtcNow)).ConfigureAwait(false);

                return Results.Ok(decision);
            })
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);
}
