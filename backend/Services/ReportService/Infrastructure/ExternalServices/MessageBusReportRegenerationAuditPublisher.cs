using AiStockTrading.Shared.Contracts.Events;
using ReportService.Features.Reports;
using Wolverine;
using Wolverine.Runtime;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-11, 計画 ADR-0052 決定 5, #1156, IADR-0491 決定 5: 作り直しの監査（ReportRegenerated）をバスへ発行する。監査サービスが中央台帳へ記録する。
// 提示の通知（MessageBusReportDraftPresentedNotifier）と同じく singleton の IWolverineRuntime から MessageBus を作る。
// 例外は握らない（呼び出し側の ReportRegenerationService が記録して続ける＝保存済みの下書きを失敗と伝えない）。
public sealed class MessageBusReportRegenerationAuditPublisher(IWolverineRuntime runtime) : IReportRegenerationAuditPublisher
{
    public async Task PublishAsync(ReportRegenerated evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        await new MessageBus(runtime).PublishAsync(evt).ConfigureAwait(false);
    }
}
