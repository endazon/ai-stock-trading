using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, IADR-0023: 市場カレンダー。市場ローカル時刻で「取引日か（週末・休場日でない）」を判定し、開場日のみサイクルを起動する。
public interface IMarketCalendar
{
    bool IsOpen(Market market, DateTimeOffset instant);

    /// <summary>
    /// FR-10, FR-19, #1396, IADR-0561 決定 1: その瞬間の市場ローカルの日付の最後の閉場（大引け。半日取引日は前倒し）。休場日は null。
    /// 閉場前の手仕舞いと判断のプロンプトの「閉場までの残り時間」が引く。既定は共有カーネルの規則計算だけ（本番の MarketCalendar は構成の
    /// 臨時休場・臨時の半日を足す）。
    /// </summary>
    DateTimeOffset? SessionClose(Market market, DateTimeOffset instant) => MarketHours.SessionCloseOn(market, instant);
}
