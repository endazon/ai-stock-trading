using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, IADR-0023: 市場カレンダー。市場ローカル時刻で「取引日か（週末・休場日でない）」を判定し、開場日のみサイクルを起動する。
public interface IMarketCalendar
{
    bool IsOpen(Market market, DateTimeOffset instant);
}
