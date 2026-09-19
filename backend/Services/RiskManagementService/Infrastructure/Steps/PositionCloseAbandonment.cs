using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Steps;

// FR-09, FR-10, FR-11, UC-06, #847, IADR-0357: 「未約定残を残して終わった手仕舞い」を 1 件の通知イベントへ写す。
//
// 2 つの終端の届き方（OrderExecuted と OrderCancelled）で同じ判定が要るため、ここに 1 つだけ置く
// —— 2 箇所へ書くと片方だけが直る（IADR-0141 の規律）。
//
// 🔴 **在庫の押さえには一切関与しない。** 押さえの解放は MarkTerminal が既に済ませており（IADR-0117 改定 1/2/4）、
// 本クラスは**解放されたという事実を利用者へ伝えるためだけ**に台帳を読む。したがって、ここでの判定が
// 誤っても二重決済は生じない（生じるのは通知の過不足だけである）。
internal static class PositionCloseAbandonment
{
    /// <summary>
    /// 終端になった承認が「手仕舞い（<see cref="PositionEffect.Close"/>）で、未約定残が残っている」なら
    /// 通知イベントを返す。そうでなければ <c>null</c>。
    /// <para>
    /// 呼び出しの前提は <b>MarkTerminal がこの呼び出しで初めて終端を記録した</b>ことである
    /// （再配送で撃ち直さないための冪等キー）。
    /// </para>
    /// </summary>
    public static PositionCloseAbandoned? Describe(
        IPortfolioLedgerStore ledger, Guid decisionId, OrderStatus terminalStatus, DateTimeOffset terminalAt)
    {
        // 台帳に承認が無い注文は語れない（AppendFill / MarkTerminal と同じ規律）。
        if (ledger.FindApprovedIntent(decisionId) is not { PositionEffect: PositionEffect.Close } intent)
            return null;

        var filled = ledger.FindApprovedFilledQuantity(decisionId) ?? 0;
        var remaining = intent.Quantity - filled;
        if (remaining <= 0)
            return null;

        return new PositionCloseAbandoned(
            decisionId, intent.Symbol, intent.Market, intent.Side,
            intent.Quantity, filled, remaining, terminalStatus, terminalAt);
    }
}
