using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace OrderExecutionService.Infrastructure.Steps;

// 🔴 FR-10, FR-05, FR-11, UC-06, #858, IADR-0370, IADR-0350 決定5:
// **利用者が承認した乖離の取り込みを受けて、発注執行側の保護記録とブローカー側の保護注文を追随させる。**
//
// サービス間は直接参照しない（IADR-0350 決定5）。リスク管理が発行する PositionDriftAdopted を購読する形で、
// 取り込みで消えた建玉の保護レグを取り消し、記録を終端化する。キューは規約発見で
// ai-stock-trading.order-execution-service.PositionDriftAdopted（IADR-0129 の共通ヘルパ・durable）。
//
// 🔴 **例外を握るのは意図である**（PositionCloseCancellationHandler は握らない——作法が違う理由を書く）。
// あちらは取消 1 件だけを行い、失敗を記録する経路が他に無いため、再試行と _error キューが唯一の可視化である。
// こちらは複数の保護レグを 1 通で処理し、取り消せなかった行は
//   ① 記録が Active のまま残る（ガードが巡回を続ける）② Critical のイベントを出す
// の 2 つで必ず可視化される。全体を投げ直すと、成功した行の処理まで捨てて再配送することになる
// （冪等なので壊れはしないが、成功と失敗が混ざった 1 通を丸ごと DLQ へ送ると、取り消せた側の記録が読みにくい）。
//
// 🔴 **ただし建玉照会が不明・失敗のときは投げる**（ProtectiveStopDriftPositionsUnknownException。
// IADR-0370 2026-09-24 追記 / PR #918 監査）。このときは**どの行にも触る前に**打ち切っているので捨てる成功は無く、
// 「建玉が消えたと確かめられないまま保護を消さない」ために、再試行（2s/10s/30s）で照会をやり直す。
// 使い切れば _error キューに残る（Critical は業務クラスがログ済み。投げた処理中の発行は Wolverine が捨てるため、ここでは発行しない）。
//
// 🔴 #942, IADR-0395: **この配送が最後か**（ここで投げると _error へ送られるか）を Wolverine の配送回数から決めて業務クラスへ渡す。
// 最後の配送の打ち切りだけが業務メトリクス ast.order.drift_adoption_followup_abandoned を増やし、アラートが鳴る。
// 配送回数（Envelope.Attempts）は受信のたびにハンドラの前で 1 増える（1 始まり。WolverineFx 6.24.5 の Executor.ExecuteAsync）。
// 共通の失敗規則は 1〜3 回目を再試行、4 回目を _error へ移す（WolverineExtensions.MaxDeliveryAttempts）。
// `>=` で比べるのは、配送回数が上限を超えて届いた場合（例: _error から戻したメッセージが attempts ヘッダを引き継いでいた場合）も、
// その失敗は規則の枠（1〜4 回目）の外であり、Wolverine の既定（FailureRule が枠を見つけられないときの MoveToErrorQueue）で
// 再び _error へ送られるため。
public sealed class PositionDriftAdoptedHandler(
    ProtectiveStopDriftAdopter adopter,
    ILogger<PositionDriftAdoptedHandler> logger)
{
    public async Task Handle(
        PositionDriftAdopted message, IMessageBus bus, Envelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(envelope);

        var finalDeliveryAttempt = IsFinalDeliveryAttempt(envelope.Attempts);

        logger.LogInformation(
            "乖離の取り込みを受けて保護記録を追随させます: 取り込み={AdoptionId} 銘柄={Symbol}/{Market}"
            + " 台帳 {Before}→{After}（観測 {Broker}・依頼者 {Actor}）配送 {Attempt}/{MaxAttempts} 回目",
            message.AdoptionId, message.Symbol, message.Market,
            message.LedgerQuantityBefore, message.LedgerQuantityAfter, message.BrokerQuantity, message.Actor,
            envelope.Attempts, WolverineExtensions.MaxDeliveryAttempts);

        var result = await adopter.ApplyAsync(message, finalDeliveryAttempt, cancellationToken).ConfigureAwait(false);

        // ADR-0013, IADR-0129, #354: Wolverine の PublishAsync は CancellationToken を取らない。
        foreach (var evt in result.Events)
            await bus.PublishAsync(evt).ConfigureAwait(false);

        if (result.Reduced > 0 || result.CancelUnconfirmed > 0)
        {
            logger.LogWarning(
                "乖離の取り込みの追随: 保護記録 {Scanned} 件を評価（減らした {Reduced} / 取消を確認できない {Unconfirmed}）。"
                + "銘柄={Symbol}/{Market}",
                result.Scanned, result.Reduced, result.CancelUnconfirmed, message.Symbol, message.Market);
        }
    }

    /// <summary>
    /// #942, IADR-0395: 配送回数 <paramref name="attempts"/>（1 始まり）の失敗で、メッセージが <c>_error</c> へ送られるか。
    /// </summary>
    public static bool IsFinalDeliveryAttempt(int attempts) =>
        attempts >= WolverineExtensions.MaxDeliveryAttempts;
}
