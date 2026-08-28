using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, UC-02, #331, IADR-0210 決定2/3: 発注執行が発注した保護レグ（逆指値・成行手仕舞い）の
// 決済 Intent を取引台帳の承認行へ結線するハンドラ群。
//
// 保護レグは**既にブローカーへ発注済み**であり、OrderApproved を流すと発注執行が二重発注するため、
// 専用イベント（ProtectiveStopPlaced / ProtectiveStopCoverageLost）で承認行だけを追加する。
// 承認行が無いと、レグの約定（OrderExecuted・発注執行の約定追跡が発行）を台帳が相関できず
// （AppendFill は承認 Intent 必須）、**損切りが成立しても台帳の建玉が減らない**。
// 冪等は AppendApproval の DecisionId 冪等がそのまま担保する（再送・ガードの再巡回で二重計上しない）。

public sealed class ProtectiveStopPlacedLedgerHandler(
    IPortfolioLedgerStore ledger,
    ILogger<ProtectiveStopPlacedLedgerHandler> logger)
{
    public void Handle(ProtectiveStopPlaced message)
    {
        ArgumentNullException.ThrowIfNull(message);

        ledger.AppendApproval(message.StopDecisionId, message.CloseIntent, message.PlacedAt);
        logger.LogDebug(
            "台帳に保護逆指値レグの承認を記録: EntryDecisionId={EntryDecisionId} StopDecisionId={StopDecisionId}"
                + " 銘柄={Symbol} トリガー={Trigger} 試行={Attempt}",
            message.EntryDecisionId, message.StopDecisionId, message.CloseIntent.Symbol,
            message.TriggerPrice, message.Attempt);
    }
}

public sealed class ProtectiveStopCoverageLostLedgerHandler(
    IPortfolioLedgerStore ledger,
    ILogger<ProtectiveStopCoverageLostLedgerHandler> logger)
{
    public void Handle(ProtectiveStopCoverageLost message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 手仕舞いレグを伴う場合のみ承認行を追加する（EntryCancelled / None は追加すべきレグが無い）。
        if (message is { CloseDecisionId: { } closeDecisionId, CloseIntent: { } closeIntent })
        {
            ledger.AppendApproval(closeDecisionId, closeIntent, message.OccurredAt);
            logger.LogDebug(
                "台帳に保護喪失の手仕舞いレグの承認を記録: EntryDecisionId={EntryDecisionId}"
                    + " CloseDecisionId={CloseDecisionId} 銘柄={Symbol} 数量={Quantity}",
                message.EntryDecisionId, closeDecisionId, message.Symbol, message.Quantity);
        }
    }
}
