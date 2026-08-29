using AiStockTrading.Shared.Contracts.Events;

using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-10, FR-17, UC-06, #381, ADR-0022 決定2・決定5, IADR-0196 決定2・決定3: 期間内の
// 為替の情報源の状態（フォールバックの発生・復帰・鮮度警告）を供給するポート。
//
// **日報だけ経路が違う。** 監査・Discord はイベントを push で受けるが、報告書は
// 「ある瞬間の通知」ではなく「**期間の集計**」であるため、他の供給ポート（IPeriodFillSource 等）と
// 同じく**期間を指定して引く**（IADR-0196 決定2）。
//
// 🔴 **供給不達を空へ倒さない。** 取得できなかった場合は <c>null</c> を返す。
// IMarginReductionRecordSource（IADR-0133 決定7）と同じ判断である——
// **「照会できなかった」を「切替は無かった」と書くことは、劣化を隠したのと同じ結果になる。**
// それは ADR-0022 決定2 の「黙って劣化させない」が禁じていることそのものである。
public interface IFxSourceStatusSource
{
    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（JST 取引日）の状態。
    /// <b>事象なしは空の <see cref="FxSourceStatus"/>、取得不能は <c>null</c>。</b>
    /// </summary>
    Task<FxSourceStatus?> GetStatusAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
