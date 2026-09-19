using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Steps;

// FR-10, FR-05, UC-06, #848, IADR-0117（2026-09-19 追記）, IADR-0067: 明示的な取消（OrderCancelled）を
// 取引台帳へ**終端**として届ける。
//
// 取消は 2 つの経路で届く:
//   1. 約定追跡（OrderFillPoller）がブローカーの状態を引き直して再発行する OrderExecuted（Status=Cancelled）
//      —— #848 で実測した経路（利用者が moomoo アプリで取り消した）。OrderExecutedLedgerHandler が受ける。
//   2. 発注執行が自ら取り消したときの OrderCancelled（OrderAmendmentDispatcher）—— 本ハンドラが受ける。
// どちらか一方だけを配線すると、片方の経路で取り消された手仕舞いが「処理中の決済」として建玉をロックし続ける。
//
// ADR-0013, IADR-0129 決定 10: 本ハンドラは OrderCancelledActivityHandler と同一のハンドラチェーンで実行される
// （Wolverine では 1 サービス内 1 イベント型 = 1 キュー）。再試行では両方が再実行されるが、双方の書き込みは
// 冪等である —— MarkTerminal は単調（既に終端なら何もしない）、RecordCancellation は絶対値の代入。
// **新しいキューは増えない**（OrderCancelled の購読は既に存在する）。
public sealed class OrderCancelledLedgerHandler(IPortfolioLedgerStore ledger)
{
    public void Handle(OrderCancelled message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 相関する承認が無い取消（他サービス由来・台帳に載らない注文）は MarkTerminal 側が無視する。
        ledger.MarkTerminal(message.DecisionId, OrderStatus.Cancelled, message.CancelledAt);
    }
}
