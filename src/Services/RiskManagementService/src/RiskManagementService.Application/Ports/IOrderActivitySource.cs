using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-19, IADR-0037: 相場操縦検知の入力＝直近の注文アクティビティ窓を供給するポート。
// RiskEvaluator は同期純関数のため同期契約とする。実注文履歴テレメトリ（発注・訂正・取消イベントの永続化 #13/#17）
// が確定するまでは InMemoryOrderActivitySource（プロセス内リングバッファ）で供給する。
public interface IOrderActivitySource
{
    /// <summary>
    /// 指定（銘柄, 市場）の [asOf - lookback, asOf] の注文アクティビティ窓を返す。
    /// 記録が無ければ空窓（最小標本ガードにより無嫌疑）を返す。
    /// </summary>
    OrderActivityWindow GetRecentActivity(string symbol, Market market, DateTimeOffset asOf, TimeSpan lookback);
}
