using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

namespace RiskManagementService.Features.RiskManagement.VerifyOwnerSecondFactor;

// NFR-06, FR-14, IADR-0626 決定 1: 第 2 要素の状態の持ち主（リスク管理）が、報告書の確定の分を代わりに確かめる口。
// POST /risk-controls/owner-second-factor/verify（サービスの身元だけ。ボット・利用者のトークンは通さない）。
//
// 🔴 報告書が持つ 2 操作（報告書の確定・方針の確定）だけを受ける。リスク管理の 5 操作は自分の門で確かめる。
// 段（Audit / Enforce）は呼び手が決める。ここは結果だけを返す（失敗の数・停止・再利用の判定は 2 サービスで 1 つ）。
// ログにコードを載せない。
internal static class VerifyOwnerSecondFactorEndpoint
{
    internal static readonly SecondFactorOperation[] DelegatedOperations =
        [SecondFactorOperation.ReportConfirm, SecondFactorOperation.PolicyConfirm];

    public static void MapVerifyOwnerSecondFactor(this IEndpointRouteBuilder g) =>
        g.MapPost("/owner-second-factor/verify", Handle)
            .RequireAuthorization(policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.IsInRole(AiStockTradingAuthPolicies.ServiceRole)
                    && !ctx.User.IsInRole(AiStockTradingAuthPolicies.BotRole)
                    && !ctx.User.IsInRole(AiStockTradingAuthPolicies.OwnerRole)));

    internal static IResult Handle(OwnerSecondFactorVerifyRequest req, IOwnerSecondFactorVerifier verifier, ILoggerFactory loggerFactory)
    {
        if (req is null
            || !Enum.TryParse<SecondFactorOperation>(req.Operation, ignoreCase: false, out var operation)
            || !DelegatedOperations.Contains(operation)
            || !Enum.GetNames<SecondFactorOperation>().Contains(req.Operation, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(req.Target)
            || req.Target.Length > OwnerSecondFactorTargets.MaxLength)
        {
            return Results.BadRequest(new { error = "operation（報告書の確定・方針の確定）と target を指定してください。" });
        }

        var outcome = verifier.Verify(operation, req.Target, req.Code);
        loggerFactory.CreateLogger("OwnerSecondFactor").Log(
            outcome == SecondFactorOutcome.Verified ? LogLevel.Information : LogLevel.Warning,
            "{Marker} operation={Operation} target={Target} outcome={Outcome} via=delegated",
            OwnerSecondFactorGate.LogMarker, operation, OwnerSecondFactorTargets.ForLog(req.Target), outcome);
        return Results.Ok(new OwnerSecondFactorVerifyResponse(outcome.ToString()));
    }
}
