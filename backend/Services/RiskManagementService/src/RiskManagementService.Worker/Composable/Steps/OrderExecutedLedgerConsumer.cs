using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;

// FR-10, FR-05, IADR-0018: 約定（OrderExecuted）を購読し、取引台帳へ記録する。銘柄・方向は先行の承認（OrderApproved）を
// DecisionId で相関して補完する。
//
// #270, IADR-0112: 記録の条件は「約定があること」（FilledQuantity > 0）であって全量約定（Filled）ではない。
// moomoo は Accepted（約定 0）→ 部分約定 → 全量約定と非同期に遷移するため、全量約定だけを待つと
// 部分約定の間ずっと台帳が空になり、次サイクルの統制（SameDayReentry・日次発注上限・段階資金上限）が素通しになる。
// 部分約定のまま取消・失効した注文（Cancelled ＋ 約定あり）を丸ごと落とす過少計上も同じ条件から生じていた。
// 数量はブローカの**累積値**であり差分ではない。冪等（同一 OrderId は単調 upsert）はストア側で担保する。
internal sealed class OrderExecutedLedgerConsumer(
    IPortfolioLedgerStore ledger,
    ILogger<OrderExecutedLedgerConsumer> logger)
    : IConsumer<OrderExecuted>
{
    public Task Consume(ConsumeContext<OrderExecuted> context)
    {
        var m = context.Message;

        // 約定していない結果（受付・失注・約定 0 の取消・拒否）は台帳に載せない。
        if (m.FilledQuantity <= 0)
            return Task.CompletedTask;

        var recorded = ledger.AppendFill(m.DecisionId, m.OrderId, m.FilledQuantity, m.AveragePrice, m.ExecutedAt);
        if (!recorded)
        {
            // 相関する承認が台帳に無い異常（承認は約定に先行して発行されるため通常は発生しない）。
            logger.LogWarning(
                "約定に相関する承認が台帳に無いため記録をスキップ: DecisionId={DecisionId} OrderId={OrderId}",
                m.DecisionId, m.OrderId);
        }

        return Task.CompletedTask;
    }
}
