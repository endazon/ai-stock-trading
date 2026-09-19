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
    /// <param name="observedFilledQuantity">
    /// #847, IADR-0357: 終端を運んだ側が<b>ブローカーから直に観測した累積約定数</b>（分からなければ 0）。
    /// 🔴 <b>取消は部分約定を追い越して台帳へ着く。</b> 取消の確認 → <c>OrderCancelled</c> は発注執行が即座に
    /// 発行するのに対し、部分約定は約定追跡（30 秒周期）経由で届くためである。台帳の累計だけで残数量を出すと
    /// 部分約定ぶんを未決済として二重に数え、しかも終端の記録は単調なので<b>訂正通知も出ない</b>。
    /// 台帳の累計との<b>大きいほう</b>を採る（過小に報告しない・単調・遅着の台帳更新で数字が戻らない）。
    /// </param>
    public static PositionCloseAbandoned? Describe(
        IPortfolioLedgerStore ledger,
        Guid decisionId,
        OrderStatus terminalStatus,
        DateTimeOffset terminalAt,
        int observedFilledQuantity = 0)
    {
        // 台帳に承認が無い注文は語れない（AppendFill / MarkTerminal と同じ規律）。
        if (ledger.FindApprovedIntent(decisionId) is not { PositionEffect: PositionEffect.Close } intent)
            return null;

        var filled = Math.Max(ledger.FindApprovedFilledQuantity(decisionId) ?? 0, observedFilledQuantity);
        var remaining = intent.Quantity - filled;
        if (remaining <= 0)
            return null;

        return new PositionCloseAbandoned(
            decisionId, intent.Symbol, intent.Market, intent.Side,
            intent.Quantity, filled, remaining, terminalStatus, terminalAt);
    }
}
