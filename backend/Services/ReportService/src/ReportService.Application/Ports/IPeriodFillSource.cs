using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Ports;

// FR-06, FR-16, IADR-0115 決定5, #280: 集計対象期間の約定を供給するポート。
// 権威源はリスク管理サービスの取引台帳（approved_orders × trade_fills）であり、Database per Service（ADR-0001）を
// 跨いだ DB 直参照はせず s2s 同期照会で取得する（IADR-0095 と同型）。
//
// 既定実装は no-op（空列）。供給不達（未設定・非 2xx・timeout・例外）は空列へ倒し、数値 0 の報告書として生成を続ける
// （報告書は発注判断を行わないため、欠測が過大発注へ繋がる経路が無い）。
public interface IPeriodFillSource
{
    /// <summary>期間 [fromInclusive, toInclusive]（JST 取引日）の約定を返す。取得不能なら空列（例外を投げない）。</summary>
    Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
