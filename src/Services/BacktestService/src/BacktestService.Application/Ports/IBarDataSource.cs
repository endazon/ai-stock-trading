using AiStockTrading.Backtest.Domain;

namespace AiStockTrading.Backtest.Application;

// FR-15, IADR-0037: 過去データ供給の抽象。指定期間の日足バーを決定的に返す。
// 実データ源コネクタ（J-Quants Free / Stooq 等）は本ポートのアダプタとして後続 Issue で差し込む。
//
// 契約: 同一 (Symbol, Market, Date) のバーは重複させず、正規の 1 本のみを返すこと。
// 万一重複した場合、シミュレータは列挙順で後に現れたバーを採用する（後勝ち）が、これはフェイルセーフであり
// アダプタが依存してよい仕様ではない（正規化はアダプタ側の責務。#99 レビュー指摘）。
public interface IBarDataSource
{
    IReadOnlyList<PriceBar> GetBars(DateOnly from, DateOnly to);
}
