using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-10: 判定時点のポートフォリオ状態（保有・当日発注累計・実現/含み損益・DD・連敗・当日取引銘柄）の供給。
// 実データは約定・損益集計（#13/#17 連携）から得る。本 PR ではポートのみ定義し、Slice B/後続で実体を配線する。
public interface IPortfolioStateProvider
{
    PortfolioState GetCurrent();
}
