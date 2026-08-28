using AiStockTrading.CostControl.Application.Ports;
using AiStockTrading.CostControl.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Observability;
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
//
// NFR-07, NFR-13, #287, IADR-0255: 費用メトリクス（計上額と月次上限の消費率）はここで計上する。
// 重複排除を通過した計上だけを数える（マークが立たなかった再配信は台帳にも積まれない）。
public sealed class LlmCostIncurredHandler(
    IProcessedMessageStore processedMessages,
    AppSvc svc,
    IClock clock,
    BusinessMetrics metrics,
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
            // NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218: **用途で対象範囲を判別する。**
            // 月次 LLM 費用上限（15,000 円）の対象は取引判断サイクルのみであり、報告書生成・情報収集は
            // LlmUncapped へ計上する（記録はするが上限には積まない・抑制もしない）。
            //
            // 🔴 同じカウンタに積むと、100% 到達で報告書生成が止まり、日報が確定せず翌営業日の取引が止まる
            // （UC-01 の事前条件）。**費用統制が取引を止める連鎖**であり、計画が名指しで禁じた形である。
            // purpose が無い（従来の形）ときは上限側へ倒す——過小計上を作らない（LlmCostScope）。
            var category = LlmCostScope.IsGoverned(message.Purpose) ? CostCategory.Llm : CostCategory.LlmUncapped;

            var result = await svc.RecordAsync(category, message.Amount, cancellationToken)
                .ConfigureAwait(false);

            // NFR-07, NFR-13, #287: 計上額（カテゴリ別）と、当月 LLM 費用が上限に占める割合を計上する。
            // Percent は LLM 上限（対象カテゴリ）に対する比率であり、対象外の計上でも現在値を再掲する
            // （ゲージは最後の観測値を持つ。対象外の計上で比率が動かないのは正しい）。
            metrics.RecordLlmCost(category.ToString(), message.Amount, result.Percent);

            // しきい値が上方遷移したら通知（/costs/record エンドポイントと同一の挙動・IADR-0027）。
            // 対象外の計上では LLM 累計が動かないため CrossedTo は構造的に null になる（RecordAsync の
            // before/after は CostCategory.Llm だけを見る）。ここでカテゴリを見て抑制する必要はない。
            if (result.CrossedTo is { } crossed)
            {
                await bus.PublishAsync(new CostThresholdReached(
                    result.Month, category.ToString(), result.Percent, crossed.ToString(), now))
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
