using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-09, FR-11, #1267, IADR-0517: Sent=false の連続を publish する実装。NotificationService（Discord の Warning /
// 回復は Info）と AuditService（台帳）が購読する。
//
// singleton として登録する（連続の状態を呼び出し・サイクルを跨いで共有する）。Wolverine の IMessageBus は scoped のため、
// singleton の IWolverineRuntime から MessageBus を作って発行する（PublishingFxSourceStatusNotifier と同じ形）。
// 時刻は TimeProvider から取る（試験は偽の時計で進め、実時間を待たない）。
public sealed class PublishingLlmGatewayUnsentNotifier(
    IWolverineRuntime runtime,
    TimeProvider timeProvider,
    ILogger<PublishingLlmGatewayUnsentNotifier> logger,
    int threshold = LlmGatewayUnsentEpisodeTracker.DefaultThreshold) : ILlmGatewayUnsentNotifier
{
    private readonly LlmGatewayUnsentEpisodeTracker _tracker = new(threshold);

    // 試験の継ぎ目（実バスを起こさずに発行の失敗・巻き戻しを確かめる）。既定は Wolverine。
    internal Func<object, Task>? PublishOverride { get; init; }

    public Task ReportUnsentAsync(
        string purpose, LlmGatewayUnsentCause cause, CancellationToken cancellationToken = default) =>
        PublishIfAny(_tracker.OnUnsent(purpose, cause, timeProvider.GetUtcNow()));

    public Task ReportSentAsync(CancellationToken cancellationToken = default) =>
        PublishIfAny(_tracker.OnSent(timeProvider.GetUtcNow()));

    // 🔴 発行に失敗したら状態を戻してから投げ直す（呼び出し側の HttpLlmCompletionClient が best-effort で握る）。
    private async Task PublishIfAny(object? evt)
    {
        if (evt is null)
            return;

        try
        {
            if (PublishOverride is { } publish)
                await publish(evt).ConfigureAwait(false);
            else
                await new MessageBus(runtime).PublishAsync(evt).ConfigureAwait(false);

            logger.LogInformation("LLM ゲートウェイの送信不可の連続を発行しました（{Event}）。", evt.GetType().Name);
        }
        catch
        {
            _tracker.Rollback(evt);
            throw;
        }
    }
}
