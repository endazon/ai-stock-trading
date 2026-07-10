using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10, FR-05, IADR-0018: 取引台帳からの純射影で PortfolioState を供給する IPortfolioStateProvider 実装。
// PlaceholderPortfolioStateProvider を置き換える。基準資金は TradingDefaults.InitialCapital（既存基準と同一）。
public sealed class LedgerPortfolioStateProvider(IPortfolioLedgerStore ledger, IClock clock)
    : IPortfolioStateProvider
{
    public PortfolioState GetCurrent() =>
        PortfolioProjection.Project(ledger.GetFills(), clock.Today, TradingDefaults.InitialCapital);
}
