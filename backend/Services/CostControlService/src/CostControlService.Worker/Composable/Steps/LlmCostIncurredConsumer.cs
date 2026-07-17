using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using AppSvc = AiStockTrading.CostControl.Application.Services.CostControlService;

namespace AiStockTrading.CostControl.Worker.Composable.Steps;

// NFR（費用）, FR-04, IADR-0055 決定1/5: LlmCostIncurred を購読し CostCategory.Llm として月次台帳へ計上する。
// HTTP /costs/record は OwnerOnly でサービストークンでは 403 のため、内部メッセージングで計上する。
// 冪等性（決定5）: MassTransit の at-least-once 再配信で重複し得るため MessageId で重複排除する。
// 計上に失敗したらマークを戻し、再配信で再試行できるようにする（マークだけ残って計上が欠落するのを避ける）。
// トランザクション（決定4）: outbox は使わず EfCostLedger の月内直列化（アドバイザリロック）に委ねる。
internal sealed class LlmCostIncurredConsumer(
    IProcessedMessageStore processedMessages,
    AppSvc svc,
    IClock clock,
    IPublishEndpoint publishEndpoint,
    ILogger<LlmCostIncurredConsumer> logger)
    : IConsumer<LlmCostIncurred>
{
    public async Task Consume(ConsumeContext<LlmCostIncurred> context)
    {
        // MessageId が無い場合は重複排除できないため、その回のみ処理する（既存 AuditConsumerHelper と同系）。
        var messageId = context.MessageId ?? Guid.NewGuid();
        var now = clock.UtcNow;

        if (!processedMessages.TryMarkProcessed(messageId, now))
        {
            logger.LogDebug("LlmCostIncurred は処理済みのため二重計上しません messageId={MessageId}", messageId);
            return;
        }

        try
        {
            var result = await svc.RecordAsync(CostCategory.Llm, context.Message.Amount, context.CancellationToken)
                .ConfigureAwait(false);

            // しきい値が上方遷移したら通知（/costs/record エンドポイントと同一の挙動・IADR-0027）。
            if (result.CrossedTo is { } crossed)
            {
                await publishEndpoint.Publish(new CostThresholdReached(
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
