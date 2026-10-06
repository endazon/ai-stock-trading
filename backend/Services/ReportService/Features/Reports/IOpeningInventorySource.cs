using AiStockTrading.Shared.Contracts.Trading;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-16, #1181, IADR-0493 決定 1・2: **期間開始時点の在庫**（市場ごと・指定した現地取引日より前までの台帳行を畳んだもの）を
// 供給するポート。権威源はリスク管理サービスの取引台帳であり、Database per Service（ADR-0001）を跨いだ DB 直参照はせず
// `GET /risk-controls/opening-inventory`（OwnerOrService）・gRPC `RiskControlsRead/GetOpeningInventory` へ s2s 同期照会する。
//
// 🔴 **不達を空列へ倒さない**（<see cref="IPeriodFillSource"/> とは向きが違う）。空列は「その時点で建玉なし」であり、
// 持ち越した建玉の決済を「期間より前の建玉が無いのに決済した」と読ませる。不達は `null`＝照会できていない
// （報告書は期間で切った在庫のまま畳み、取得原価を要する値を「算出不能」と描く。IADR-0493 決定 4）。
public interface IOpeningInventorySource
{
    /// <summary>
    /// <paramref name="market"/> の、現地取引日が <paramref name="beforeTradingDay"/> <b>より前</b>（排他）までの台帳行を畳んだ在庫。
    /// <b><c>null</c>＝照会できていない／空列＝その時点で建玉なし。</b>例外は投げない。
    /// </summary>
    Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
        Market market,
        DateOnly beforeTradingDay,
        CancellationToken cancellationToken = default);
}
