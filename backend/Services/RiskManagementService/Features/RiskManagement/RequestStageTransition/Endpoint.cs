using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.Kernel.Trading;
using RiskManagementService.Domain;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement.RequestStageTransition;

// ---- 段階ゲートの遷移（FR-20, UC-06, ADR-0008, IADR-0041/0070）: 利用者のみ（OwnerOnly）----
// 承認による昇格・差し戻し。承認欠如時の遷移は純ドメインが構造的に拒否する。
// 認可は owner サブグループに付与し親グループには付けない（親は 403）。Discord（UC-06）承認は #15 Bot 基盤が
// trading-owner マップで本 OwnerOnly エンドポイントを呼ぶ（kill switch と同型）。
//
// FR-20, FR-11, FR-14, IADR-0240 決定11, IADR-0383, #868: **承認者は DelegatedActorResolver が決める。**
// Discord Bot は owner マップ機密クライアント（client_credentials）のトークンで呼ぶため、トークンの主体は人ではない。
// 本文の OnBehalfOf（代理される利用者）は**信頼するクライアントのトークンに限って**採り、利用者トークン直叩き・
// 一覧外のクライアントでは無視する（承認のなりすまし防止）。
//
// 🔴 **承認者を特定できない遷移は行わない。** 段階昇格は FR-20 の実資金ゲートの承認記録であり、7 年保持の台帳
// （FR-11）に `unknown` が残ると「誰が実資金への移行を承認したか」が失われる（#868 の症状）。
// `StageGate.RequestTransition` の承認者検査は空文字しか見ないため、ここで 400 に倒す（台帳もイベントも触らない）。
internal static class RequestStageTransitionEndpoint
{
    public static void MapRequestStageTransition(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/stage-gate/transition",
            async (StageTransitionRequest req, StageGateService svc, IMessageBus bus,
                DelegatedActorOptions delegated, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            var approver = DelegatedActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
            var approverLogger = loggerFactory.CreateLogger("StageTransitionApprover");

            // 信頼クライアントの代理指定が値域外。**承認者を記録できない承認は行わない**（状態にも触れない）。
            if (approver.Rejected)
            {
                approverLogger.LogWarning(
                    "段階遷移の代理される利用者（OnBehalfOf）が値域外のため遷移を拒否しました（Approval={Approval}）。",
                    LogSanitizer.Sanitize((req.Approval ?? StageApprovalKind.StageTransition).ToString()));
                return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
            }

            // 🔴 **承認者をまったく特定できない**（名前クレームも azp も無いトークン）。`unknown` を台帳へ残さない。
            if (approver.IsUnidentified)
            {
                approverLogger.LogWarning("段階遷移の承認者を特定できないため遷移を拒否しました。");
                return Results.BadRequest(new
                {
                    error = "承認者を特定できないため段階遷移を行いません（トークンに利用者名がありません）。",
                });
            }

            // 代理指定を信じなかった（利用者トークン直叩き・一覧外のクライアント・azp 欠落・一覧未設定）。
            // 遷移は通すが、なりすましの試行／設定漏れのどちらも見えるよう警告に残す。
            if (approver.IgnoredOnBehalfOf)
            {
                approverLogger.LogWarning(
                    "段階遷移の OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。承認者={Actor}）。",
                    LogSanitizer.Sanitize(approver.Actor));
            }

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

                result = svc.RecordShortSellReleaseVerdict(approver.Actor);
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

                result = svc.RequestTransition(target, approver.Actor);
            }

            // FR-11, #167, IADR-0082: 受理時のみ中央監査集約のため StageTransitioned を発行する（拒否時は非発行）。
            // 永続化（Risk 専有台帳 stage_transitions）はサービス内で先に完了しており、それが権威（fail-safe）。
            // 段階/種別は Shared.Contracts が Risk.Domain へ依存しないよう primitive（int/文字列）へ写す。
            if (result is { Accepted: true, Transition: { } t })
            {
                // FR-11, #466, §4.1 追補3（Q13-b）, IADR-0180: **警告を無視して昇格した事実**を監査へ残す。
                // 昇格に絞らず**受理された遷移すべて**に載せる——絞ると降格の記録が「設定不明」になり、
                // null が「昇格ではなかった」と「供給されなかった」の両方を意味してしまう。
                //
                // FR-11, #868, IADR-0240 決定11, IADR-0383: 代理承認（Discord Bot 経由）では
                // `ApprovedBy`＝操作した利用者・`AuthorizedBy`＝認可の主体（クライアント ID）の**両方**を運ぶ。
                // 利用者本人のトークンでは `AuthorizedBy` は null（末尾の任意項目・後方互換の追加のみ＝IADR-0079）。
                await bus.PublishAsync(new StageTransitioned(
                    t.Sequence, (int)t.FromStage, (int)t.ToStage, t.Kind.ToString(), t.ApprovedBy, t.Reason, t.OccurredAtUtc,
                    result.Stage1Criteria.MinimumTradeCount, result.Stage1Criteria.BelowStatisticalBasis,
                    approver.AuthorizedBy));
            }

            // 受理は 200、受理不能な遷移（未充足基準・飛び級・現段階指定）は 422 に写像する。
            return result.Accepted ? Results.Ok(result) : Results.UnprocessableEntity(result);
        });
}

// FR-20, UC-06: 段階ゲート遷移の要求。TradingStage は既定 JSON では数値で往復する。TargetStage は nullable とし、
// 省略や範囲外値をエンドポイントで 400 に弾く（省略時に既定値 0＝Stage 0 として暗黙処理されるのを防ぐ）。
//
// FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: Approval は**承認種別**（省略＝段階遷移・後方互換）。
// 空売り実弾解禁の verdict は本要求へ相乗りし、専用エンドポイントを作らない。
//
// FR-20, FR-11, FR-14, #868, IADR-0240 決定11, IADR-0383: OnBehalfOf（任意・**末尾に追加**）は、呼び出し元が
// **代理している利用者**（Keycloak 利用者名）。Discord Bot が多層認証で解決した操作者を載せる。
// 🔴 **信頼するクライアントのトークン以外では無視される**（`DelegatedActorResolver`）——本文の名前をそのまま
// 承認者にすると、利用者トークン直叩きで他人の名前を実資金ゲートの承認者にできてしまう。
internal sealed record StageTransitionRequest(
    TradingStage? TargetStage, StageApprovalKind? Approval = null, string? OnBehalfOf = null);

// FR-20, ADR-0016 決定14, #388, IADR-0281 決定1: POST /stage-gate/transition が受け付ける承認の種別。
// **序数は HTTP 経路で整数として往来する**ため、値を明示し追加は末尾へ行う。
internal enum StageApprovalKind
{
    /// <summary>段階遷移（昇格・差し戻し）。省略時の既定＝従来どおりの振る舞い。</summary>
    StageTransition = 0,

    /// <summary>空売り実弾解禁の verdict（段階は動かさない）。</summary>
    ShortSellReleaseVerdict = 1,
}
