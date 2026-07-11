using AiStockTrading.Backtest.Domain;

namespace AiStockTrading.Backtest.Application;

// FR-15, IADR-0037: 過去データ供給の抽象。指定期間の日足バーを決定的に返す。
// 実データ源コネクタ（J-Quants Free / Stooq 等）は本ポートのアダプタとして後続 Issue で差し込む。
public interface IBarDataSource
{
    IReadOnlyList<PriceBar> GetBars(DateOnly from, DateOnly to);
}
