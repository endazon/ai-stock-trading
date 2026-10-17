using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-11, ADR-0068 決定4, #1373, IADR-0548 決定5: 方針の応答（REST・gRPC・as-of）の期間キーと版番号から、判断の記録へ載せる
// 出所を作る。🔴 **欠けた・読めない値は null**（0 や空の期間キーで「記録された」ふりをしない。原則 A）。方針そのものは止めない。
public static class DailyPolicyReferences
{
    public static DailyPolicyReference? From(string? periodKey, int? version) =>
        !string.IsNullOrWhiteSpace(periodKey) && version is > 0
            ? new DailyPolicyReference(periodKey, version.Value)
            : null;
}
