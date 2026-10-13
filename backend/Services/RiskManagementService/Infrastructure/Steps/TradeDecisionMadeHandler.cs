using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace RiskManagementService.Infrastructure.Steps;

// FR-10, UC-01, UC-02, ADR-0003: 取引判断（TradeDecisionMade）を購読し、発注前に決定的に検証して
// OrderApproved / OrderRejected を発行する。判定ロジックは Application 層の OrderScreeningService に委譲する。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<TradeDecisionMade> から Wolverine のハンドラへ移行した。
// **本サービスは OrderApproved を発行しつつ自分でも購読している**（台帳・活動射影）。Wolverine の既定では
// 発行が自プロセス内に閉じ、OrderExecutionService へ一通も届かない（発注が一件も執行されない）。
// これを止めるのが IADR-0129 決定 3（DisableConventionalLocalRouting・共通ヘルパが強制する）である。
//
// NFR-07, #287, IADR-0255: 統制の健全性メトリクス（審査回数と拒否理由の内訳）はここで計上する。
public sealed class TradeDecisionMadeHandler(
    OrderScreeningService screeningService,
    IControlViolationObservationStore violationStore,
    BusinessMetrics metrics,
    ILogger<TradeDecisionMadeHandler> logger)
{
    public async Task Handle(TradeDecisionMade message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        // FR-10, #967, IADR-0425 決定7: 審査は非同期（新規の売り建てでは借株可否を発注執行へ照会する）。
        var outcome = await screeningService.ScreenAsync(message, cancellationToken).ConfigureAwait(false);

        // FR-10, #832, IADR-0407: 承認済みの新規建ての判断の再配送。**何も発行しない**（承認を発行し直さない・拒否へ反転させない）。
        // 観測は最初の審査が発行より先に記録済みであり、審査メトリクスも新しい審査ではないので刻まない。
        // 例外にもしない（投げると共通再試行が同じ判断を再処理するだけで、結論は変わらない）。
        if (outcome.IsApprovedReplay)
        {
            logger.LogInformation(
                "承認済みの判断の再配送のため再審査しません: DecisionId={DecisionId}", message.DecisionId);
            return;
        }

        // 🔴 FR-10, #1319 の監査, IADR-0529 決定6: 予約はあるが承認行が無い判断の再配送で、承認時刻の取引日が過ぎていた。
        // 何も発行しない（前の取引日の判断を今日の新規建てにしない）・観測も記録しない（拒否に当たる状態は無い）。
        // 新規建てを止める状態（kill switch・ロックアウト・一時停止）のときは拒否の形で返り、下の通常の拒否の経路を通る。
        if (outcome.IsReservationExpired)
        {
            logger.LogWarning(
                "予約済みの判断の再配送ですが、承認時刻の取引日が過ぎたため承認を発行し直しません: DecisionId={DecisionId}",
                message.DecisionId);
            return;
        }

        // 🔴 FR-10, #1319, IADR-0529 決定4: 予約はあるが承認行がまだ無い新規建ての再配送。予約から組み直した**最初と同じ承認**を
        // 発行し直す（最初の発行が失敗していれば、これを出さないと発注が永久に出ない。届いていた場合の重複は発注執行が
        // DecisionId で止める・IADR-0057）。観測は記録する（DecisionId で冪等。最初の記録の後・発行の前に落ちた場合を埋める）。
        // 審査メトリクスは刻まない（新しい審査ではない）。
        if (outcome.IsApprovedRepublish)
        {
            violationStore.Record(outcome.Observation);
            logger.LogWarning(
                "予約済みで承認行が無い判断の再配送のため、再審査せず同じ承認を発行し直します: DecisionId={DecisionId} 数量={Quantity}",
                message.DecisionId, outcome.Approved!.ApprovedQuantity);
            await bus.PublishAsync(outcome.Approved!).ConfigureAwait(false);
            return;
        }

        // FR-20, FR-11, #387, IADR-0148: 審査結果を段階ゲートの観測ログへ記録する（承認・拒否のいずれも）。
        // **イベント発行より先に記録する**——発行が失敗して再送されても DecisionId で冪等であり、
        // 逆順にすると「発行できたが観測が落ちた」拒否が生まれ、違反件数が過小になる（緩い側）。
        violationStore.Record(outcome.Observation);

        // NFR-07, #287: 承認・拒否の**いずれでも**審査 1 件として数える。拒否だけを数えると
        // 「違反 0 件」と「そもそも審査が動いていない」を区別できない（観測ログと同じ理由・#387）。
        metrics.RecordOrderScreening(
            outcome.IsApproved,
            outcome.IsApproved ? [] : outcome.Rejected!.Reasons);

        if (outcome.IsApproved)
        {
            logger.LogInformation(
                "注文承認: DecisionId={DecisionId} 数量={Quantity}",
                message.DecisionId, outcome.Approved!.ApprovedQuantity);
            await bus.PublishAsync(outcome.Approved!).ConfigureAwait(false);
        }
        else
        {
            // FR-11: 拒否理由は監査・通知のため理由列挙つきで発行する。
            logger.LogWarning(
                "注文拒否: DecisionId={DecisionId} 理由={Reasons}",
                message.DecisionId, string.Join(",", outcome.Rejected!.Reasons));
            await bus.PublishAsync(outcome.Rejected!).ConfigureAwait(false);
        }
    }
}
