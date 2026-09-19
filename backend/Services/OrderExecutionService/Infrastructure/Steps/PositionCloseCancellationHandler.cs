using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace OrderExecutionService.Infrastructure.Steps;

// 🔴 FR-05, FR-10, FR-11, UC-06, #847, #768, IADR-0357:
// **利用者（owner）の「板に残った手仕舞いを取り消す」要求を受けて、実際にブローカーへ取消を送る。**
//
// これが #768（`OrderAmendmentDispatcher` は DI 登録だけで本番の呼び出し元が無い）の配線である。
// #847 はそれが実運用で実害になった最初の事例であり、稼働環境（2026-09-18 23:13 JST）では板に残った
// 手仕舞い（3,381 株・指値 334.09）を消す手段が **moomoo アプリだけ**だった。
//
// 例外は握らない —— 取消の失敗（未知の DecisionId・終端済みの注文をブローカーが拒否した等）は
// Wolverine の再試行（2s/10s/30s）を経て `_error` キューへ落ちる。**「取り消せた」と誤って主張しない**ことが
// ここでの最優先であり、握って正常終了すると利用者は消えたと思い込む。
//
// 🔴 在庫の押さえを解く `OrderCancelled` は `OrderAmendmentDispatcher` が**確認できたときだけ**発行する
//（IADR-0117 改定 1/4）。本ハンドラはそこへ委ね、自分では 1 つもイベントを作らない。
public sealed class PositionCloseCancellationHandler(
    OrderAmendmentDispatcher dispatcher,
    ILogger<PositionCloseCancellationHandler> logger)
{
    public async Task Handle(PositionCloseCancellationRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        logger.LogInformation(
            "利用者による手仕舞いの取消要求: DecisionId={DecisionId} 銘柄={Symbol}/{Market} 依頼者={Actor} 理由={Reason}",
            message.DecisionId, message.Symbol, message.Market, message.Actor, message.Reason);

        // 理由は監査の本体（PositionCloseCancellationRequested）が持つが、注文ライフサイクル台帳
        //（order_lifecycle）にも誰の要求だったかが残るよう、依頼者を添えて渡す。
        await dispatcher
            .CancelAsync(message.DecisionId, $"{message.Reason}（利用者 {message.Actor} による取消要求）",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
