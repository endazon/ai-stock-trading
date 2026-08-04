using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, FR-05, IADR-0018: 約定（OrderExecuted）を購読し、取引台帳へ記録する。銘柄・方向は先行の承認（OrderApproved）を
// DecisionId で相関して補完する。
//
// #270, IADR-0113: 記録の条件は「約定があること」（FilledQuantity > 0）であって全量約定（Filled）ではない。
// moomoo は Accepted（約定 0）→ 部分約定 → 全量約定と非同期に遷移するため、全量約定だけを待つと
// 部分約定の間ずっと台帳が空になり、次サイクルの統制（SameDayReentry・日次発注上限・段階資金上限）が素通しになる。
// 部分約定のまま取消・失効した注文（Cancelled ＋ 約定あり）を丸ごと落とす過少計上も同じ条件から生じていた。
// 数量はブローカの**累積値**であり差分ではない。冪等（同一 OrderId は単調 upsert）はストア側で担保する。
//
// ADR-0013, IADR-0129 決定 10, #354: **本ハンドラは OrderExecutedActivityHandler と同一のハンドラチェーンで実行される。**
// 再試行では両方が再実行されるが、双方の書き込みは冪等である（IADR-0129 決定 10 に根拠を記載）:
//   - 本ハンドラ: AppendFill は累積数量の単調 upsert（`filledQuantity <= existing` なら無変更）
//   - 相方:       RecordExecution は同一メッセージの再適用で同じ状態になる絶対値の代入
public sealed class OrderExecutedLedgerHandler(
    IPortfolioLedgerStore ledger,
    ILogger<OrderExecutedLedgerHandler> logger)
{
    public void Handle(OrderExecuted message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 約定していない結果（受付・失注・約定 0 の取消・拒否）は台帳に載せない。
        if (message.FilledQuantity <= 0)
            return;

        var recorded = ledger.AppendFill(
            message.DecisionId, message.OrderId, message.FilledQuantity, message.AveragePrice, message.ExecutedAt);
        if (!recorded)
        {
            // 相関する承認が台帳に無い異常（承認は約定に先行して発行されるため通常は発生しない）。
            logger.LogWarning(
                "約定に相関する承認が台帳に無いため記録をスキップ: DecisionId={DecisionId} OrderId={OrderId}",
                message.DecisionId, message.OrderId);
        }
    }
}
