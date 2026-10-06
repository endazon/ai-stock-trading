using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-16, IADR-0115 決定5, #280: 集計対象期間の約定を供給するポート。
// 権威源はリスク管理サービスの取引台帳（approved_orders × trade_fills）であり、Database per Service（ADR-0001）を
// 跨いだ DB 直参照はせず s2s 同期照会で取得する（IADR-0095 と同型）。
//
// 既定実装は no-op（空列）。供給不達（未設定・非 2xx・timeout・例外）は空列へ倒し、数値 0 の報告書として生成を続ける
// （報告書は発注判断を行わないため、欠測が過大発注へ繋がる経路が無い）。
public interface IPeriodFillSource
{
    /// <summary>
    /// 約定の市場の現地取引日（米国＝ET・東証＝JST。IADR-0246）が [fromInclusive, toInclusive] に入る約定を返す。
    /// 取得不能なら空列（例外を投げない）。報告書の期間（JST の営業日）をそのまま渡さない —— 呼び出し側は
    /// ReportSchedule.SessionWindowOf の外包で引き、市場ごとに絞る（#1172・IADR-0492）。
    /// </summary>
    Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
