using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Infrastructure.Composable.Steps;

// NFR（費用）, FR-04, IADR-0055 決定1/5: LlmCostIncurred を購読し CostCategory.Llm として月次台帳へ計上する。
// HTTP /costs/record は OwnerOnly でサービストークンでは 403 のため、内部メッセージングで計上する。
// 冪等性（決定5）: at-least-once の再配信で重複し得るため MessageId で重複排除する。
// 計上に失敗したらマークを戻し、再配信で再試行できるようにする（マークだけ残って計上が欠落するのを避ける）。
// トランザクション（決定4）: outbox は使わず EfCostLedger の月内直列化（アドバイザリロック）に委ねる。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<LlmCostIncurred> から Wolverine のハンドラへ移行した。
// - `ConsumeContext<T>` は消え、メッセージ本体・`Envelope`・`IMessageBus` をメソッド引数で受け取る。
// - `context.MessageId`（`Guid?`）は `envelope.Id`（`Guid`・非 null）になる。Wolverine は送信時に必ず ID を採番するため、
//   MassTransit 時代にあった「MessageId が無ければ重複排除できない」分岐は**構造的に不要**になる。
// - **public であること自体が要件である**（Wolverine は public なハンドラ型しか発見しない。実測）。
public sealed class LlmCostIncurredHandler(
    IProcessedMessageStore processedMessages,
    AppSvc svc,
    IClock clock,
    ILogger<LlmCostIncurredHandler> logger)
{
    public async Task Handle(
        LlmCostIncurred message,
        Envelope envelope,
        IMessageBus bus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(bus);

        var messageId = envelope.Id;
        var now = clock.UtcNow;

        if (!processedMessages.TryMarkProcessed(messageId, now))
        {
            logger.LogDebug("LlmCostIncurred は処理済みのため二重計上しません messageId={MessageId}", messageId);
            return;
        }

        try
        {
            var result = await svc.RecordAsync(CostCategory.Llm, message.Amount, cancellationToken)
                .ConfigureAwait(false);

            // しきい値が上方遷移したら通知（/costs/record エンドポイントと同一の挙動・IADR-0027）。
            if (result.CrossedTo is { } crossed)
            {
                await bus.PublishAsync(new CostThresholdReached(
                    result.Month, CostCategory.Llm.ToString(), result.Percent, crossed.ToString(), now))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 計上できなかったのでマークを戻し、再配信で再試行できるようにする。
            processedMessages.Unmark(messageId);
            throw;
        }
    }
}
