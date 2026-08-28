using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03: 銘柄別の最終トリガー時刻を保持し、クールダウン中の再トリガーを抑制する。
public interface ICooldownStore
{
    DateTimeOffset? GetLastTriggered(string symbol, Market market);

    void SetLastTriggered(string symbol, Market market, DateTimeOffset at);
}
