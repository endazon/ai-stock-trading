using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace AiStockTrading.RiskManagement.Api.Foundation.Endpoints;

// FR-10, FR-19, FR-20, UC-06, ADR-0003, ADR-0007, ADR-0008: kill switch 操作・リスク設定変更の HTTP エンドポイント。
// 書き込み系は OwnerOnly（利用者のみ・Keycloak ロール trading-owner）を要求する。actor は認証済みトークンの名前
// （preferred_username）を用いる。生成AI・自動処理はこのロールを持たないため変更できない。
// IADR-0051: 読み取り系の同期照会（sizing-context / open-positions）はサービス間 s2s（trading-service）でも呼べる
// よう OwnerOrService に分離する（サービスへ書き込み権限は与えない＝最小権限）。
internal static class RiskControlEndpoints
{
    public static IEndpointRouteBuilder MapRiskControlEndpoints(this IEndpointRouteBuilder app)
    {
        // 例外→HTTP マッピング（読み書き共通）: アクター/理由欠如などの検証失敗は 400、設定の楽観排他競合（IADR-0012）は 409。
        // これらを既定の 500 にせず、クライアントが区別できるステータスに写像する。
        var g = app.MapGroup("/risk-controls")
            .WithTags("RiskControls")
            .AddEndpointFilter(async (ctx, next) =>
            {
                try
                {
                    return await next(ctx);
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Conflict(new { error = "設定が他の更新と競合しました。最新を取得して再試行してください。" });
                }
            });

        // ---- 読み取り系: 利用者またはサービス（IADR-0051・OwnerOrService） ----
        // 未認証は 401、trading-owner/trading-service いずれも持たなければ 403。
        var read = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);

        // サイジング文脈（FR-04/10, IADR-0029）: 取引判断（#11）が同期照会する（設定＋ポートフォリオ状態から導出）。
        read.MapGet("/sizing-context", (SizingContextService svc) => Results.Ok(svc.Build()));

        // 保有ポジション（FR-03/10, IADR-0030）: 市場監視（#10）が損切りライン検知のため同期照会する
        // （#63 台帳の射影＋損切り価格の近似導出）。
        read.MapGet("/open-positions", (OpenPositionsService svc) => Results.Ok(svc.Build()));

        // 期間の約定（FR-06/16, IADR-0115 決定5, #280）: 報告書サービス（#14）が日報/週報/月報の数値集計のため
        // 同期照会する。取引台帳（承認 Intent × 約定）を取引日（JST 境界）で絞って返す。読み取り専用で、
        // 新規テーブル・新規イベントは持たない。期間が逆順・未指定でも 200（空列）＝報告書生成を止めない。
        read.MapGet("/fills", (DateOnly? from, DateOnly? to, IPortfolioLedgerStore ledger) =>
        {
            if (from is not { } fromDay || to is not { } toDay)
                return Results.BadRequest(new { error = "from・to（yyyy-MM-dd）は必須です。" });

            return Results.Ok(PeriodFillQuery.InTradingDayRange(ledger.GetFills(), fromDay, toDay));
        });

        // ---- 利用者のみ（kill switch は ADR-0003・ガード設定は ADR-0007・段階は ADR-0008／OwnerOnly）: kill switch・設定変更。サービスには許可しない ----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        owner.MapGet("/kill-switch", (KillSwitchService svc) => Results.Ok(svc.GetState()));

        owner.MapPost("/kill-switch/engage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) =>
        {
            svc.Engage(ActorOf(http), req.Reason);
            return Results.Ok(svc.GetState());
        });

        owner.MapPost("/kill-switch/disengage", (KillSwitchRequest req, KillSwitchService svc, HttpContext http) =>
        {
            svc.Disengage(ActorOf(http), req.Reason);
            return Results.Ok(svc.GetState());
        });

        // ---- 取引の一時停止/再開（FR-10, FR-14, UC-06, ADR-0009）: 利用者のみ（OwnerOnly）。理由必須 ----
        // pause は kill switch より軽い統制で、`/resume` は pause のみ解除する（日次損失ロックアウトは解除しない）。
        // 操作は冪等（停止中の再 pause・非停止中の resume は現状態を返すのみ）。actor は認証済みトークン名。
        owner.MapGet("/pause", (PauseService svc) => Results.Ok(svc.GetState()));

        owner.MapPost("/pause", (PauseRequest req, PauseService svc, HttpContext http) =>
            Results.Ok(svc.Pause(ActorOf(http), req.Reason)));

        owner.MapPost("/resume", (PauseRequest req, PauseService svc, HttpContext http) =>
            Results.Ok(svc.Resume(ActorOf(http), req.Reason)));

        // ---- 建玉の手仕舞い（FR-10, FR-11, UC-06, #292, IADR-0117）: 利用者のみ（OwnerOnly）。理由必須 ----
        // FR-10 本文「kill switch・日次損失ロックアウト・一時停止は…いずれも手仕舞い（Close）と損切りは止めない」に従い、
        // 発注前スクリーニング（OrderScreeningService）を通さない（損切りの機械執行と同型・IADR-0015）。
        // サービストークンには開かない（生成AI・自動処理が建玉を落とせないようにする＝FR-10「生成AIはこれらを上書きできない」・ADR-0003）。
        owner.MapPost("/positions/close",
            async (PositionCloseRequest req, PositionCloseService svc, IMessageBus bus, HttpContext http) =>
        {
            // market は Market?（非 nullable enum は省略時に暗黙 0＝日本市場へ束縛され、誤市場の決済を通してしまう）。
            if (string.IsNullOrWhiteSpace(req.Symbol))
                return Results.BadRequest(new { error = "symbol（銘柄コード）は必須です。" });
            if (req.Market is not { } market || !Enum.IsDefined(market))
                return Results.BadRequest(new { error = "market は有効な市場（Japan/UnitedStates）を指定してください。" });
            if (string.IsNullOrWhiteSpace(req.Reason))
                return Results.BadRequest(new { error = "reason（理由）は必須です。" });

            var outcome = svc.Request(
                new PositionCloseCommand(req.Symbol, market, req.Quantity, req.LimitPrice, req.Reason),
                ActorOf(http));

            if (!outcome.Accepted)
            {
                var error = DescribeRejection(outcome.Rejection);
                return outcome.Rejection == PositionCloseRejection.PositionNotFound
                    ? Results.NotFound(new { error })
                    : Results.UnprocessableEntity(new { error });
            }

            // FR-11: 監査（誰が・なぜ）を先に発行する。OrderApproved はアクターも理由も持たないため、これが無いと
            // 手仕舞いの操作者が監査台帳に残らない。順序の根拠は「起きた操作に監査が無い」より「監査があるのに操作が
            // 無い」ほうが安全（後者は同一 DecisionId の後続イベント不在で検知できる）。
            await bus.PublishAsync(outcome.Requested!);
            await bus.PublishAsync(outcome.Approval!);

            // 約定は後から非同期に成立するため 202（200 ではない）。以降は既存経路（発注執行 → OrderExecuted → 台帳 → 通知）。
            var intent = outcome.Approval!.Intent;
            return Results.Accepted(value: new
            {
                decisionId = outcome.Approval.DecisionId,
                symbol = intent.Symbol,
                market = intent.Market,
                side = intent.Side,
                quantity = intent.Quantity,
                price = intent.Price,
                mode = intent.Mode,
            });
        });

        // ---- 稼働状態の集約照会（FR-10, UC-07, ADR-0009）: 表示専用。Discord `/status` が参照する ----
        // 3 統制の状態・優先順位・段階・当日損益・上限使用率・ポジションを 1 回で返す（設定変更は含まない）。
        // 認可は OwnerOnly とする（読み取り系だが sizing-context / open-positions の OwnerOrService とは分ける）。
        // 理由: 当日損益・ポジション等の機微情報を束ねた利用者向けサマリであり、サービス（trading-service）に開く
        // 用途が無いため。最小権限（IADR-0051）に従い、必要のない読み取り権限をサービスへ与えない。
        owner.MapGet("/status", (RiskStatusService svc) => Results.Ok(svc.Build()));

        // ---- 設定（FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008） ----
        owner.MapGet("/settings", (RiskSettingsService svc) => Results.Ok(svc.GetCurrent()));

        owner.MapGet("/settings/history", (ISettingsChangeLog changeLog) => Results.Ok(changeLog.GetHistory()));

        owner.MapPut("/settings/limits", (LimitsUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateLimits(req.Limits, ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });

        owner.MapPut("/settings/stage", (StageUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateStage(req.Stage, ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });

        owner.MapPut("/settings/guard", (GuardUpdateRequest req, RiskSettingsService svc, HttpContext http) =>
        {
            svc.UpdateGuard(req.ToGuardSettings(), ActorOf(http), req.Reason);
            return Results.Ok(svc.GetCurrent());
        });

        // ---- 段階ゲート（FR-20, UC-06, ADR-0008, IADR-0041/0070）: 利用者のみ（OwnerOnly）----
        // 承認による昇格・差し戻し。承認者＝認証済み利用者名。承認欠如時の遷移は純ドメインが構造的に拒否する。
        // 認可は owner サブグループに付与し親グループには付けない（親は 403）。Discord（UC-06）承認は #15 Bot 基盤が
        // trading-owner マップで本 OwnerOnly エンドポイントを呼ぶ（kill switch と同型・Bot ハンドラは後続）。
        owner.MapGet("/stage-gate", (StageGateService svc) => Results.Ok(svc.GetStatus()));

        owner.MapGet("/stage-gate/history", (StageGateService svc) => Results.Ok(svc.GetHistory()));

        owner.MapPost("/stage-gate/transition",
            async (StageTransitionRequest req, StageGateService svc, IMessageBus bus, HttpContext http) =>
        {
            // 値域検証: targetStage の省略（null）や範囲外 enum は 400。範囲外（負値・4 以上）の降格方向は
            // StageGate 側の連番検証を素通りし StageGatePolicy.SettingsFor で KeyNotFoundException（500）になり得るため、
            // サービス到達前に弾く（省略時の暗黙 Stage 0 差し戻しも防ぐ）。
            if (req.TargetStage is not { } target || !Enum.IsDefined(target))
            {
                return Results.BadRequest(new { error = "targetStage は有効な運用段階（Stage 0〜3）を指定してください。" });
            }

            var result = svc.RequestTransition(target, ActorOf(http));

            // FR-11, #167, IADR-0082: 受理時のみ中央監査集約のため StageTransitioned を発行する（拒否時は非発行）。
            // 永続化（Risk 専有台帳 stage_transitions）はサービス内で先に完了しており、それが権威（fail-safe）。
            // 段階/種別は Shared.Contracts が Risk.Domain へ依存しないよう primitive（int/文字列）へ写す。
            if (result is { Accepted: true, Transition: { } t })
            {
                await bus.PublishAsync(new StageTransitioned(
                    t.Sequence, (int)t.FromStage, (int)t.ToStage, t.Kind.ToString(), t.ApprovedBy, t.Reason, t.OccurredAtUtc));
            }

            // 受理は 200、受理不能な遷移（未充足基準・飛び級・現段階指定）は 422 に写像する。
            return result.Accepted ? Results.Ok(result) : Results.UnprocessableEntity(result);
        });

        // 撤退基準の評価＋自動安全側（HaltNewEntries なら kill switch を自動起動）。実降格は行わず降格提案を返す。
        // 応答は撤退判定（Assessment）のみ（NewlyEngaged はドライバ #166 用の内部フラグ・API 契約は不変）。
        owner.MapPost("/stage-gate/withdrawal/evaluate",
            (StageGateService svc) => Results.Ok(svc.EvaluateWithdrawal().Assessment));

        return app;
    }

    // #292, IADR-0117: 決済要求の拒否理由を利用者向け文言に写す（HTTP ステータスは呼び出し側で分ける）。
    private static string DescribeRejection(PositionCloseRejection rejection) => rejection switch
    {
        PositionCloseRejection.PositionNotFound => "該当する建玉がありません（全決済済みを含む）。",
        PositionCloseRejection.InvalidQuantity => "quantity は 1 以上を指定してください。",
        PositionCloseRejection.ExceedsAvailable =>
            "決済数量が利用可能数量（建玉 − 処理中の決済）を超えています。処理中の決済の約定を待って再試行してください。",
        PositionCloseRejection.PriceUnavailable =>
            "決済価格を決められません（現在値を取得できず limitPrice も指定されていません）。limitPrice を指定してください。",
        _ => "決済要求を受理できません。",
    };

    // 認証済みトークンの名前（preferred_username）。OwnerOnly を通過している前提だが、null は unknown に倒す。
    private static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}

// kill switch 操作の要求（理由必須・FR-11・ADR-0003）。
internal sealed record KillSwitchRequest(string Reason);

// 一時停止/再開操作の要求（理由必須・FR-11・ADR-0009）。
internal sealed record PauseRequest(string Reason);

// #292, IADR-0117: 建玉の手仕舞い要求（理由必須）。売買方向は含めない（建玉方向からサーバが決める）。
// Market は nullable。非 nullable enum は本文省略時に既定値 0（＝Japan）へ暗黙束縛され、意図しない市場の建玉を
// 対象にしてしまうため、省略を 400 として弾けるようにする。
// Quantity 省略＝処理中を除いた残り全量。LimitPrice 省略＝現在値。
internal sealed record PositionCloseRequest(
    string? Symbol,
    Market? Market,
    int? Quantity,
    decimal? LimitPrice,
    string? Reason);

// 上限変更の要求。RiskLimitSettings は具象プロパティのレコードで標準の逆直列化が可能。
internal sealed record LimitsUpdateRequest(RiskLimitSettings Limits, string Reason);

// 段階変更の要求。
internal sealed record StageUpdateRequest(StageSettings Stage, string Reason);

// FR-20, UC-06: 段階ゲート遷移の要求。承認者は要求本文ではなく認証済みトークン（OwnerOnly）から取る
// （承認なりすまし防止）。TradingStage は既定 JSON では数値で往復する。TargetStage は nullable とし、省略や
// 範囲外値をエンドポイントで 400 に弾く（省略時に既定値 0＝Stage 0 として暗黙処理されるのを防ぐ）。
internal sealed record StageTransitionRequest(TradingStage? TargetStage);

// ガード変更の要求。TradingGuardSettings は IReadOnlySet 等を用いるため、逆直列化可能な具象コレクションで受ける。
internal sealed record GuardUpdateRequest(
    List<ProductType> EnabledProductTypes,
    List<Market> EnabledMarkets,
    List<BannedSymbol> BannedSymbols,
    bool PreventSameDayReentry,
    bool ProhibitManipulativeOrderPatterns,
    string Reason)
{
    public TradingGuardSettings ToGuardSettings() => new()
    {
        EnabledProductTypes = new HashSet<ProductType>(EnabledProductTypes),
        EnabledMarkets = new HashSet<Market>(EnabledMarkets),
        BannedSymbols = BannedSymbols,
        PreventSameDayReentry = PreventSameDayReentry,
        ProhibitManipulativeOrderPatterns = ProhibitManipulativeOrderPatterns,
    };
}
