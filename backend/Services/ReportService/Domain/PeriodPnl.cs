using AiStockTrading.Shared.Kernel.Trading;

namespace ReportService.Domain;

// FR-06, FR-16, 計画 ADR-0059 決定 3, #1218, IADR-0519 決定 3: **報告書の期間の損益集計の入口を 1 つにする。**
// 週報 §1「週間実現損益（税引後・費用込み）」（ReportDraftService が全種別の §1 に使う）と、日報 §6 の週初来の実現損益が
// **同じ関数・同じ前提条件・同じ在庫の扱い**で数えられることを、呼び出し先が 1 つであることで保つ。
// 週の最終営業日の日報 §6 の値と週報 §1 の値が一致する（計画 ADR-0059 決定 3）のは、窓（ReportSchedule.SessionWindowOf）と本関数が同じだからである。
public static class PeriodPnl
{
    /// <summary>
    /// 期間の損益を集計する。<paramref name="openingInventoryUnknown"/> は期間開始時点の在庫を照会できなかったこと
    /// （生成器が未供給と判定した。#1181, IADR-0493 決定 4）で、真なら取得原価を要する値を部分値として印を付ける。
    /// </summary>
    public static PnlSummary Aggregate(
        IReadOnlyList<PeriodTradeFill> fills,
        IReadOnlyDictionary<string, decimal>? currentPrices,
        IReadOnlyList<PeriodDriftAdoption>? adoptions,
        OpeningInventorySnapshot? opening,
        bool openingInventoryUnknown)
    {
        // 前提条件は暫定で既定値（#19 バージョン付き取得・#63 台帳連携は #22 後続）。
        var pnl = PnlAggregator.Aggregate(fills, TradingAssumptionsDefaults.Create(), currentPrices, adoptions, opening);
        return openingInventoryUnknown ? pnl with { OpeningInventoryUnknown = true } : pnl;
    }
}
