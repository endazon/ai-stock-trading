namespace RiskManagementService.Features.RiskManagement;

// FR-06, FR-16, FR-10, #611, 05_trading-assumptions §3（実現損益＝約定時レート）, IADR-0107 決定2, IADR-0282 決定1:
// 承認記録時の**認識時レート**（基準通貨〔USD〕1 単位あたりの表示通貨〔JPY〕額＝1 USD あたりの円）を解決するポート。
//
// 承認は取引判断の直後・約定の直前であり、IADR-0107 決定2 が「承認時点の換算レート＝約定時レートの近似」と定めた
// 既存の規律と同じ時点である。承認台帳（approved_orders.FxRateBaseToDisplay）へ固定し、後から引き直さない。
//
// 🔴 **なぜ取引判断の Intent（OrderIntent）に載せないのか**: 承認台帳へ届く Intent は取引判断由来だけではない。
// 保護逆指値レグ・保護喪失の手仕舞いレグは発注執行が再構成し、発注執行は為替レート源を持たない。
// 承認記録の漏斗（AppendApproval の全呼び出し）で解決すれば、機械執行の決済にも同じ規則で入る。
//
// fail-safe: 解決できない（源が無い・取得不可・鮮度切れ・例外）ときは null＝未記録。**承認記録は止めない**。
// 未記録の約定は報告書の為替差損益で「未記録 N 件」と明記され、推定では埋まらない。
public interface IRecognitionFxRateResolver
{
    /// <summary>1 USD あたりの円。解決できなければ null（未記録）。例外を投げない（取り消しは伝播）。</summary>
    Task<decimal?> ResolveBaseToDisplayAsync(CancellationToken cancellationToken = default);
}
