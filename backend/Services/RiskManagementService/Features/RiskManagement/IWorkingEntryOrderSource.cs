using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, #829, IADR-0346 決定1: 承認済みで**まだ生きている（終端でない）新規建て注文**の供給。
//
// 計画 FR-10 は 1 日あたりの上限を「新規建ての**発注代金**の合計」で定める。約定（trade_fills）だけを数えると、
// 指値が溜まっている間は枠が減らず、上限を超えて承認し続ける（#829 の実測）。本ポートはその「発注済みで生きている」
// 注文を返し、残数量の算入は純関数 PortfolioProjection.Project が行う（約定と同じ入力から二重計上なく出すため）。
//
// 台帳（IPortfolioLedgerStore）の契約は変えない。台帳は約定の純射影（IADR-0018）であり、注文の生死は
// 注文アクティビティ（IADR-0067）の関心である。読み取り側の結合だけをここに閉じる。
public interface IWorkingEntryOrderSource
{
    /// <summary>
    /// <paramref name="approvedAtOrAfter"/> 以降に承認された新規建て（<see cref="PositionEffect.Open"/>）のうち、
    /// 注文アクティビティが<b>終端でない</b>もの（終端時刻が無い・射影の行がまだ無い）を返す。
    /// 当日の判定はしない（呼び出し側の純関数が市場の現地取引日で判定する）。下限は走査量の上限にすぎない。
    /// </summary>
    IReadOnlyList<WorkingEntryOrder> GetWorkingEntryOrders(DateTimeOffset approvedAtOrAfter);
}
