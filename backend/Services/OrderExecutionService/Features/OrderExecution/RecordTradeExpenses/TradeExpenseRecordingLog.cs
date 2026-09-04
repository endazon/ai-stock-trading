using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;

// FR-11, ADR-0016 決定15, ADR-0027 決定4, #633, IADR-0300:
// 経費記録の結果を 1 行で残す（**約定を観測する 2 経路で同じ文言を使う**ための単一情報源）。
//
// 段 1 では供給が無いため、本番に出るのは常に「未供給」の行である —— これが issue #633 の成果物であり、
// 「経費を照会する経路が無い」から「照会したが取得できないと分かっている」への変化を外から見える形にする。
// 段 2（実費の供給）が入るまで、経費が 1 件も計上されない事実はこの行以外に現れない。
public static class TradeExpenseRecordingLog
{
    /// <summary>記録の結果をログへ残す。照会しなかった場合は何も残さない（約定していない注文が大半のため）。</summary>
    public static void Write(ILogger logger, OrderExecuted executed, TradeExpenseRecordingOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(executed);
        ArgumentNullException.ThrowIfNull(outcome);

        if (outcome.Summary is not { } summary)
        {
            return;
        }

        if (outcome.IsUnavailable)
        {
            // 🔴 「費用は 0 だった」と書かない。7 区分すべてが未計上（LineCount = 0）であることを明示する。
            logger.LogWarning(
                "経費明細を照会できません: 建玉=({Symbol},{Market}) 注文={OrderId} 理由={Reason}。"
                    + " 経費区分 {CategoryCount} 種すべてを未計上（明細 0 件）として扱います"
                    + "（区分の分からない費用を既存区分へ丸めません）。",
                summary.Symbol, summary.Market, executed.OrderId, outcome.UnavailableReason,
                summary.Totals.Count);
            return;
        }

        logger.LogInformation(
            "経費明細を記録します: 建玉=({Symbol},{Market}) 注文={OrderId} 明細={LineCount} 件"
                + " 費用合計={TotalExpensesUsd} USD。",
            summary.Symbol, summary.Market, executed.OrderId, outcome.Events.Count, summary.TotalExpensesUsd);
    }
}
