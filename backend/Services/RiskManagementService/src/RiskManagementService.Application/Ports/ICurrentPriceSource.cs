using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-10, #81, IADR-0066: 時価評価（含み損益・DD）の入力となる現在値の供給。
// 判定は同期経路（OrderScreeningService → PortfolioSnapshotBuilder → IPortfolioStateProvider.GetCurrent）にあり、
// 発注判断のレイテンシに市況取得のネットワーク往復を持ち込めないため、本ポートは**同期**とし、実装は非同期に
// 補充された手元の値（QuoteCache）を読むだけにする（補充は QuoteRefreshService）。
public interface ICurrentPriceSource
{
    /// <summary>
    /// 建玉の現在値を (銘柄, 市場) → 現在値 で返す。取得できない建玉はキーを含めない（＝当該建玉の含みは 0）。
    /// </summary>
    IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(IReadOnlyList<OpenPosition> positions);
}
