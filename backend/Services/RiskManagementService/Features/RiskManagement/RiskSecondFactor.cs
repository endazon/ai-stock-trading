using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;

namespace RiskManagementService.Features.RiskManagement;

// NFR-06, FR-14, #1542, IADR-0615: 危険な操作の第 2 要素の門を、REST と gRPC が共有する処理関数から呼ぶ。
// 門が登録されていなければ（単体の試験の DefaultHttpContext など）何もしない。Enforce で通らなければ例外 → 403。
internal static class RiskSecondFactor
{
    // 🔴 IADR-0615 決定 9: 門は「状態を変える直前」にだけ呼ぶ。処理関数は本関数が返す callback をサービスへ渡し、サービスが
    // 入力の検証・冪等の判定（存在しない対象・受理できない要求・既に済んだ再送）を通った後で呼ぶ。そうでない要求はコードを消費しない。
    internal static Action? BeforeCommit(HttpContext http, SecondFactorOperation operation, string? code)
    {
        var gate = http.RequestServices?.GetService<OwnerSecondFactorGate>();
        if (gate is null || gate.Mode == SecondFactorMode.Off)
            return null;

        return () =>
        {
            var decision = gate.Check(operation, code);
            if (!decision.Allowed)
                throw new SecondFactorRejectedException(decision.Outcome);
        };
    }
}
