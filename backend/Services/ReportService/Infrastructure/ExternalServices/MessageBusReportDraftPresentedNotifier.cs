using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Contracts.Events;
using Wolverine;
using Wolverine.Runtime;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06/07/09, IADR-0116 決定1/2, #280: 提示（確定依頼）を ReportDraftPresented としてバスへ発行する。
// 通知サービスが購読して Discord へ投稿し、監査サービスが中央台帳へ集約する。
//
// 常駐（BackgroundService）の巡回はスコープを作って呼ぶが、本アダプタ自体は singleton である。
// ADR-0013, IADR-0129, #354: MassTransit の IBus（singleton）に対応する singleton の発行口は Wolverine に無く、
// **IMessageBus は scoped** である（実測: singleton へ注入すると DI のスコープ検証で起動時に落ちる）。
// singleton から発行するときは singleton の IWolverineRuntime から MessageBus を作る（Wolverine の想定用法）。
// 旧名 MassTransitReportDraftPresentedNotifier から改名した（実装の同一性は保つ）。
//
// ここでは例外を握らず素直に投げる。呼び出し側（ReportAutoGenerator）が捕捉して生成・提示を巻き戻さないまま
// NotificationFailed として結果に載せ、常駐が警告ログに残す（異常を黙って捨てない）。
public sealed class MessageBusReportDraftPresentedNotifier(IWolverineRuntime runtime, IClock clock)
    : IReportDraftPresentedNotifier
{
    public async Task NotifyAsync(PresentedReportNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // Wolverine の PublishAsync は CancellationToken を取らない（送信は封筒をアウトゴーイングへ渡して完了する）。
        await new MessageBus(runtime)
            .PublishAsync(new ReportDraftPresented(
                notice.PeriodKey,
                notice.Kind.ToString(),
                notice.PeriodLabel,
                notice.Summary,
                notice.Version,
                clock.UtcNow))
            .ConfigureAwait(false);
    }
}
