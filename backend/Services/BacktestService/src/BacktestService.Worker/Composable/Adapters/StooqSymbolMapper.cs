using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Worker.Composable.Adapters;

// FR-15, ADR-0004, #208, IADR-0105: 社内の銘柄表現（"AAPL" / "7203" ＋ Market）から Stooq の銘柄記法への写像（純関数）。
// Stooq は市場サフィックス付きの小文字表記（米国株 = <symbol>.us、日本株 = <code>.jp）で銘柄を指定する。
// 未対応の市場・空の銘柄は false を返し、誤った記法のまま外部を叩かない（fail-safe）。
public static class StooqSymbolMapper
{
    public static bool TryMap(string symbol, Market market, out string stooqSymbol)
    {
        var trimmed = (symbol ?? "").Trim();
        var suffix = market switch
        {
            Market.UnitedStates => "us",
            Market.Japan => "jp",
            _ => null,
        };

        if (trimmed.Length == 0 || suffix is null)
        {
            stooqSymbol = "";
            return false;
        }

        stooqSymbol = $"{trimmed.ToLowerInvariant()}.{suffix}";
        return true;
    }
}
