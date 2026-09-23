using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using AiStockTrading.Shared.Contracts.Events;
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
public sealed class PositionDriftAdoptedHandler(
    ProtectiveStopDriftAdopter adopter,
    ILogger<PositionDriftAdoptedHandler> logger)
{
    public async Task Handle(PositionDriftAdopted message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        logger.LogInformation(
            "乖離の取り込みを受けて保護記録を追随させます: 取り込み={AdoptionId} 銘柄={Symbol}/{Market}"
            + " 台帳 {Before}→{After}（観測 {Broker}・依頼者 {Actor}）",
            message.AdoptionId, message.Symbol, message.Market,
            message.LedgerQuantityBefore, message.LedgerQuantityAfter, message.BrokerQuantity, message.Actor);

        var result = await adopter.ApplyAsync(message, cancellationToken).ConfigureAwait(false);

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
}
