using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Kernel.Trading;
using RiskManagementService.Domain;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement.RequestStageTransition;

// ---- 段階ゲートの遷移（FR-20, UC-06, ADR-0008, IADR-0041/0070）: 利用者のみ（OwnerOnly）----
// 承認による昇格・差し戻し。承認者＝認証済み利用者名。承認欠如時の遷移は純ドメインが構造的に拒否する。
// 認可は owner サブグループに付与し親グループには付けない（親は 403）。Discord（UC-06）承認は #15 Bot 基盤が
// trading-owner マップで本 OwnerOnly エンドポイントを呼ぶ（kill switch と同型・Bot ハンドラは後続）。
internal static class RequestStageTransitionEndpoint
{
    public static void MapRequestStageTransition(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/stage-gate/transition",
            async (StageTransitionRequest req, StageGateService svc, IMessageBus bus, HttpContext http) =>
        {
            // FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: **空売り実弾解禁の verdict も本エンドポイントに相乗りする。**
            // 裁定が「段階ゲートの承認記録と同じ経路に載せる。別記録にしない」と定めたためであり、
            // **verdict 専用のエンドポイントは作らない**（構造テスト ShortSellReleaseVerdictRideAlongTests が固定する）。
            // approval の省略は従来どおりの段階遷移（後方互換）。
            var approval = req.Approval ?? StageApprovalKind.StageTransition;
            if (!Enum.IsDefined(approval))
            {
                return Results.BadRequest(new { error = "approval は有効な承認種別を指定してください。" });
            }

            StageTransitionResult result;
            if (approval == StageApprovalKind.ShortSellReleaseVerdict)
            {
                // 段階は動かさないため targetStage は取らない。**同時指定は 400 で弾く**——
                // 「昇格のつもりが verdict だけ記録された」を黙って通さない。
                if (req.TargetStage is not null)
                {
                    return Results.BadRequest(new
                    {
                        error = "空売り実弾解禁の verdict は段階を動かしません。targetStage を指定しないでください。",
                    });
                }

                result = svc.RecordShortSellReleaseVerdict(RiskControlEndpoints.ActorOf(http));
            }
            else
            {
                // 値域検証: targetStage の省略（null）や範囲外 enum は 400。範囲外（負値・4 以上）の降格方向は
                // StageGate 側の連番検証を素通りし StageGatePolicy.SettingsFor で KeyNotFoundException（500）になり得るため、
                // サービス到達前に弾く（省略時の暗黙 Stage 0 差し戻しも防ぐ）。
                if (req.TargetStage is not { } target || !Enum.IsDefined(target))
                {
                    return Results.BadRequest(new { error = "targetStage は有効な運用段階（Stage 0〜3）を指定してください。" });
                }

                result = svc.RequestTransition(target, RiskControlEndpoints.ActorOf(http));
            }

            // FR-11, #167, IADR-0082: 受理時のみ中央監査集約のため StageTransitioned を発行する（拒否時は非発行）。
            // 永続化（Risk 専有台帳 stage_transitions）はサービス内で先に完了しており、それが権威（fail-safe）。
            // 段階/種別は Shared.Contracts が Risk.Domain へ依存しないよう primitive（int/文字列）へ写す。
            if (result is { Accepted: true, Transition: { } t })
            {
                // FR-11, #466, §4.1 追補3（Q13-b）, IADR-0180: **警告を無視して昇格した事実**を監査へ残す。
                // 昇格に絞らず**受理された遷移すべて**に載せる——絞ると降格の記録が「設定不明」になり、
                // null が「昇格ではなかった」と「供給されなかった」の両方を意味してしまう。
                await bus.PublishAsync(new StageTransitioned(
                    t.Sequence, (int)t.FromStage, (int)t.ToStage, t.Kind.ToString(), t.ApprovedBy, t.Reason, t.OccurredAtUtc,
                    result.Stage1Criteria.MinimumTradeCount, result.Stage1Criteria.BelowStatisticalBasis));
            }

            // 受理は 200、受理不能な遷移（未充足基準・飛び級・現段階指定）は 422 に写像する。
            return result.Accepted ? Results.Ok(result) : Results.UnprocessableEntity(result);
        });
}

// FR-20, UC-06: 段階ゲート遷移の要求。承認者は要求本文ではなく認証済みトークン（OwnerOnly）から取る
// （承認なりすまし防止）。TradingStage は既定 JSON では数値で往復する。TargetStage は nullable とし、省略や
// 範囲外値をエンドポイントで 400 に弾く（省略時に既定値 0＝Stage 0 として暗黙処理されるのを防ぐ）。
//
// FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: Approval は**承認種別**（省略＝段階遷移・後方互換）。
// 空売り実弾解禁の verdict は本要求へ相乗りし、専用エンドポイントを作らない。
internal sealed record StageTransitionRequest(TradingStage? TargetStage, StageApprovalKind? Approval = null);

// FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: POST /stage-gate/transition が受け付ける承認の種別。
// **序数は HTTP 経路で整数として往来する**ため、値を明示し追加は末尾へ行う。
internal enum StageApprovalKind
{
    /// <summary>段階遷移（昇格・差し戻し）。省略時の既定＝従来どおりの振る舞い。</summary>
    StageTransition = 0,

    /// <summary>空売り実弾解禁の verdict（段階は動かさない）。</summary>
    ShortSellReleaseVerdict = 1,
}
