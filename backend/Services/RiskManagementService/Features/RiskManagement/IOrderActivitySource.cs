using RiskManagementService.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-19, IADR-0040/0067: 相場操縦検知の入力＝直近の注文アクティビティ窓を供給するポート。
// RiskEvaluator は同期純関数のため同期契約とする。本番は注文履歴テレメトリ（注文系イベントの Risk 専有 DB への
// 射影・#154）を読む EfOrderActivitySource が供給する。単体テスト・ローカル実行では InMemoryOrderActivityStore
// （射影＋読み取り）または InMemoryOrderActivitySource（事前構築した記録のリングバッファ）を用いる。
public interface IOrderActivitySource
{
    /// <summary>
    /// 指定（銘柄, 市場）の [asOf - lookback, asOf] の注文アクティビティ窓を返す。
    /// 記録が無ければ空窓（最小標本ガードにより無嫌疑）を返す。
    /// </summary>
    OrderActivityWindow GetRecentActivity(string symbol, Market market, DateTimeOffset asOf, TimeSpan lookback);
}
