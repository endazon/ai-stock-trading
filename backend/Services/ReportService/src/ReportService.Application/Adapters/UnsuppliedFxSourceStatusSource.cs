using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Adapters;

// FR-06, FR-10, UC-06, #381, IADR-0196 決定3, IADR-0199: 為替の状態供給の既定（**未供給＝null**）。
//
// 🔴 **空の FxSourceStatus を返さない。** 空は「期間内に劣化は無かった」という主張であり、
// **照会していない状態でそれを書くのは、劣化を隠したのと同じ結果になる**（IADR-0196 決定3）。
// `null` を返すことで、報告書が「状態を照会できませんでした（要確認）」と明記する。
//
// 🔴 **`NoMarginReductionRecordSource`（空列＝発動なし）とは向きが逆である。**
// あちらは**発火元が未実装で 1 度も発動し得ない**ため「なし」が事実として正しいが、
// **為替のイベントは本番で実際に発行されている**——「なし」と書けば端的に嘘になる。**揃えてはならない。**
public sealed class UnsuppliedFxSourceStatusSource : IFxSourceStatusSource
{
    public Task<FxSourceStatus?> GetStatusAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<FxSourceStatus?>(null);
}
