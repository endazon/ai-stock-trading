using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-16, #611, 05_trading-assumptions §3, ADR-0022, IADR-0285 決定2: 為替差損益の**期末レート**を供給するポート。
// 「期末日以前の直近の日次観測（1 USD あたりの円）」を観測日つきで返す。
//
// **供給不達を空へ倒さない。** 為替差損益は**期末に建玉が残る期間**では期末レートが無いと集計できず、
// 無いまま 0 円と書けば「為替では損得が無かった」と読める。取得できなければ null を返し、
// 報告書に「供給されていません（0 円ではありません）」と明記させる（IADR-0250 の継ぎ目と同じ向き）。
//
// 既定実装は Unsupplied（常に null）。実供給は Fx:Provider（判断サービスと同じ構成キー）で opt-in する。
public interface IPeriodEndFxRateSource
{
    /// <summary>
    /// 期末日 <paramref name="periodEnd"/> 以前の直近の日次観測を返す。解決できない・鮮度切れ・観測日が期末より後なら null。
    /// </summary>
    Task<PeriodEndFxRate?> GetRateAsync(DateOnly periodEnd, CancellationToken cancellationToken = default);
}
