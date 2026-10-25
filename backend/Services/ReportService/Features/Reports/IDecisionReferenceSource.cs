using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-08, 04_report-templates 日報 §5, #1356, IADR-0541 決定 2: 期間の取引判断が見た参考情報（ニュース・開示）を供給するポート。
// 権威源は監査台帳の判断の記録（TradeDecisionMade・TradeDecisionHeld の References）。報告書生成時に KB を検索し直さない。
// 🔴 **供給不達は null（未供給）**。判断 0 件・参照 0 件（空の Items）と混ぜない（ITradeRationaleSource と同じ向き）。
public interface IDecisionReferenceSource
{
    /// <summary>期間 [from, to]（JST の暦日）の判断の記録。取得不能なら <c>null</c>（例外を投げない）。窓での絞り込みと畳み込みは呼び出し側。</summary>
    Task<IReadOnlyList<DecisionReferenceFeedBuilder.DecisionRecord>?> GetDecisionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
}
