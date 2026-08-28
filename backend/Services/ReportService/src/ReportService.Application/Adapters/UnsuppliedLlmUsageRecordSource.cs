using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.Adapters;

// FR-06, #338, IADR-0254: 監査台帳へ結線されていない構成の安全既定。**常に null（未供給）**。
//
// 🔴 空の LlmUsageRecord（＝事象なし）を返さない。返すと「当月は LLM を 1 度も使わず費用 0 円だった」と
// 報告書が主張することになる——実際には**照会先が設定されていないだけ**である（#282 と同型の誤読）。
public sealed class UnsuppliedLlmUsageRecordSource : ILlmUsageRecordSource
{
    public Task<LlmUsageRecord?> GetUsageAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        Task.FromResult<LlmUsageRecord?>(null);
}
