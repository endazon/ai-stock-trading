using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;
using RiskManagementService.Features.RiskManagement.ClearGoodFaithViolations;
using RiskManagementService.Features.RiskManagement.ClosePosition;
using RiskManagementService.Features.RiskManagement.DisengageKillSwitch;
using RiskManagementService.Features.RiskManagement.EngageKillSwitch;
using RiskManagementService.Features.RiskManagement.EvaluateWithdrawal;
using RiskManagementService.Features.RiskManagement.GetBuyInInferences;
using RiskManagementService.Features.RiskManagement.GetFills;
using RiskManagementService.Features.RiskManagement.GetKillSwitch;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Features.RiskManagement.GetPause;
using RiskManagementService.Features.RiskManagement.GetRiskSettings;
using RiskManagementService.Features.RiskManagement.GetRiskStatus;
using RiskManagementService.Features.RiskManagement.GetSessionUptime;
using RiskManagementService.Features.RiskManagement.GetSettingsHistory;
using RiskManagementService.Features.RiskManagement.GetShortSellingStatus;
using RiskManagementService.Features.RiskManagement.GetSizingContext;
using RiskManagementService.Features.RiskManagement.GetStageGate;
using RiskManagementService.Features.RiskManagement.GetStageGateHistory;
using RiskManagementService.Features.RiskManagement.PauseTrading;
using RiskManagementService.Features.RiskManagement.RequestStageTransition;
using RiskManagementService.Features.RiskManagement.ResumeTrading;
using RiskManagementService.Features.RiskManagement.UpdateBrokerProvider;
using RiskManagementService.Features.RiskManagement.UpdateRiskLimits;
using RiskManagementService.Features.RiskManagement.UpdateStage1MinimumTradeCount;
using RiskManagementService.Features.RiskManagement.UpdateStageSettings;
using RiskManagementService.Features.RiskManagement.UpdateTradingGuard;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, FR-19, FR-20, UC-06, ADR-0003, ADR-0007, ADR-0008: kill switch 操作・リスク設定変更の HTTP エンドポイント。
// 書き込み系は OwnerOnly（利用者のみ・Keycloak ロール trading-owner）を要求する。actor は認証済みトークンの名前
// （preferred_username）を用いる。生成AI・自動処理はこのロールを持たないため変更できない。
// IADR-0051: 読み取り系の同期照会（sizing-context / open-positions）はサービス間 s2s（trading-service）でも呼べる
// よう OwnerOrService に分離する（サービスへ書き込み権限は与えない＝最小権限）。
//
// NFR, platform ADR-0068 決定1: **本ファイルは「登録表」である。** `MapGroup` ／ タグ ／ グループ単位の認可・
// フィルタ ／ `Program.cs` から呼ぶメソッド名（MapRiskControlEndpoints）はここに残す —— これらは集約の全操作が
// 使うものであり、特定の 1 操作に属さない。**個々の操作の処理は 3 段目（`<操作>/Endpoint.cs`）にある。**
// 登録の順序も動かさない（ルート登録順・タグ付け・フィルタ適用順を変えないため）。
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

        read.MapGetSizingContext();
        read.MapGetOpenPositions();
        read.MapGetFills();
        read.MapGetBuyInInferences();
        read.MapGetSessionUptime();

        // ---- 利用者のみ（kill switch は ADR-0003・ガード設定は ADR-0007・段階は ADR-0008／OwnerOnly）: kill switch・設定変更。サービスには許可しない ----
        var owner = g.MapGroup("").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOnly);

        owner.MapGetKillSwitch();
        owner.MapEngageKillSwitch();
        owner.MapDisengageKillSwitch();

        // ---- 取引の一時停止/再開（FR-10, FR-14, UC-06, ADR-0009）: 利用者のみ（OwnerOnly）。理由必須 ----
        owner.MapGetPause();
        owner.MapPauseTrading();
        owner.MapResumeTrading();

        owner.MapClosePosition();
        owner.MapClearGoodFaithViolations();
        owner.MapGetRiskStatus();
        owner.MapGetShortSellingStatus();

        // ---- 設定（FR-10/FR-19/FR-20, ADR-0003/ADR-0007/ADR-0008） ----
        owner.MapGetRiskSettings();
        owner.MapGetSettingsHistory();
        owner.MapUpdateRiskLimits();
        owner.MapUpdateStageSettings();
        owner.MapUpdateTradingGuard();
        owner.MapUpdateBrokerProvider();
        owner.MapUpdateStage1MinimumTradeCount();

        // ---- 段階ゲート（FR-20, UC-06, ADR-0008, IADR-0041/0070） ----
        read.MapGetStageGate();
        owner.MapGetStageGateHistory();
        owner.MapRequestStageTransition();
        owner.MapEvaluateWithdrawal();

        return app;
    }

    // 認証済みトークンの名前（preferred_username）。OwnerOnly を通過している前提だが、null は unknown に倒す。
    // **2 段目に残る共通部分**（platform ADR-0068 決定3）——書き込み系の全操作が使う。
    internal static string ActorOf(HttpContext http) =>
        http.User.Identity?.Name is { Length: > 0 } name ? name : "unknown";
}
