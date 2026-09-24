using AiStockTrading.Shared.Contracts.Observability;

namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-10, NFR-07, #891, IADR-0374: 取引判断が発注意図を作らなかった（見送った）ことを、
// **理由つきで**観測経路へ渡すポート。既定は NoOpDecisionSkipReporter（何もしない）で、
// Worker が計上実装（MetricsDecisionSkipReporter）を配線する。
//
// なぜポートか: 判断サービスの構築点はテストに 30 か所あり、BusinessMetrics を必須引数で足すと
// 本件の射程（観測可能性）に対して変更が大きすぎる。既存の IScreeningReductionReporter /
// IDailyPolicyUnconfirmedNotifier と同じ作法（省略可能・既定 NoOp・Worker が配線）に揃える。
//
// 🔴 **同期・戻り値なしである。** 実装は Counter.Add だけで I/O も例外も持たない。非同期にすると
// 見送り 12 地点すべてに await と fail-safe が要り、**判断の見送りに新しい失敗経路を持ち込む**。
public interface IDecisionSkipReporter
{
    void Report(string trigger, DecisionSkipReason reason);
}
