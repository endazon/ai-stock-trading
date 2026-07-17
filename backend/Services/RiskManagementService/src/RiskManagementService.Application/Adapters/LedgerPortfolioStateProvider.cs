using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10, FR-05, IADR-0018: 取引台帳からの純射影で PortfolioState を供給する IPortfolioStateProvider 実装。
// PlaceholderPortfolioStateProvider を置き換える。基準資金は TradingDefaults.InitialCapital（既存基準と同一）。
//
// #81, IADR-0065: currentPrices を注入すると含み損益・DD を時価で算出する。未注入（既定 null）では従来どおり
// 含み 0・DD 0 のまま＝現行挙動を保つ（Worker 側の RiskOptions:EnableMarkToMarket が既定 false のため既定は未注入）。
public sealed class LedgerPortfolioStateProvider(
    IPortfolioLedgerStore ledger,
    IClock clock,
    ICurrentPriceSource? currentPrices = null)
    : IPortfolioStateProvider
{
    public PortfolioState GetCurrent()
    {
        var fills = ledger.GetFills();

        if (currentPrices is null)
            return PortfolioProjection.Project(fills, clock.Today, TradingDefaults.InitialCapital);

        var prices = currentPrices.GetCurrentPrices(PortfolioProjection.ProjectOpenPositions(fills));
        var state = PortfolioProjection.Project(fills, clock.Today, TradingDefaults.InitialCapital, prices);

        // 現在エクイティ＝当日開始基準（初期資金＋当日より前の実現）＋当日実現＋含み（Project の定義と同一）。
        // ピークは台帳から再計算し（IADR-0065）、DD だけを差し替える（射影を二度走らせない）。
        var equity = state.Capital + state.DailyRealizedPnl + state.UnrealizedPnl;
        var highWaterMark = PortfolioValuation.EquityHighWaterMark(fills, TradingDefaults.InitialCapital, equity);

        return state with { DrawdownRatio = PortfolioValuation.DrawdownRatio(highWaterMark, equity) };
    }
}
