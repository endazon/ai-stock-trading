using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;

namespace AiStockTrading.Report.Application.Adapters;

// FR-10, FR-06, UC-06, #330, IADR-0133: 自動縮小の記録供給の既定（空列＝発動なし）。
//
// 自動縮小の発火元（維持率・純資産・必要証拠金の供給）が未実装であり、**現状は 1 度も発動し得ない**ため、
// 「発動なし」は事実として正しい（照会失敗を意味する null とは区別する）。権威源への結線は #331 が入れる。
public sealed class NoMarginReductionRecordSource : IMarginReductionRecordSource
{
    public Task<IReadOnlyList<MaintenanceMarginReductionExecuted>?> GetReductionsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MaintenanceMarginReductionExecuted>?>([]);
}
