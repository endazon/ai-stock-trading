using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;

namespace RiskManagementService.Features.RiskManagement;

// NFR-06, FR-14, #1542, IADR-0615: 危険な操作の第 2 要素の門を、REST と gRPC が共有する処理関数から呼ぶ。
// 門が登録されていなければ（単体の試験の DefaultHttpContext など）何もしない。Enforce で通らなければ例外 → 403。
internal static class RiskSecondFactor
{
    internal static void Require(HttpContext http, SecondFactorOperation operation, string? code)
    {
        var gate = http.RequestServices?.GetService<OwnerSecondFactorGate>();
        if (gate is null)
            return;

        var decision = gate.Check(operation, code);
        if (!decision.Allowed)
            throw new SecondFactorRejectedException(decision.Outcome);
    }
}
