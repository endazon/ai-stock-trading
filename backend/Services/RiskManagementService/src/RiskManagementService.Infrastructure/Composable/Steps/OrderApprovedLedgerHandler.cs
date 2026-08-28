using RiskManagementService.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// FR-10, FR-05, IADR-0018: 承認済み注文（OrderApproved）を購読し、Intent（銘柄・方向・建玉効果）を DecisionId で
// 取引台帳に記録する。後続の OrderExecuted（銘柄・方向を持たない）を DecisionId で相関して補完するための土台。
// 通常経路（TradeDecisionMadeHandler）・機械執行の決済（owner 手仕舞い・維持率自動縮小）の承認を統一的に取り込む
// （保護レグ〔逆指値・#331〕の承認行は ProtectiveStopLedgerHandlers が別イベントから追加する）。
//
// ADR-0013, IADR-0129 決定 10, #354: **本ハンドラは OrderApprovedActivityHandler と同一のハンドラチェーンで実行される。**
// MassTransit ではキューが分かれていたため片方の失敗が他方の再試行を引き起こさなかったが、Wolverine は
// 同じメッセージ型のハンドラを 1 本のチェーンにまとめる。したがって再試行では**両方が再実行される**。
// これを安全にしているのは、双方の書き込みが冪等であることである（IADR-0129 決定 10 に根拠を記載）:
//   - 本ハンドラ: EfPortfolioLedgerStore.AppendApproval は `ApprovedOrders.Find(decisionId)` が非 null なら return（無変更）
//   - 相方:       EfOrderActivityStore.RecordPlacement は `OrderActivities.Find(decisionId)` が非 null なら return（無変更）
// 両者は同一の RiskManagementDbContext（同一 DB）へ別テーブルを書くため、片方だけが恒久的に失敗する
// 現実的な故障モードは無い（DB 障害は双方を等しく失敗させる）。
public sealed class OrderApprovedLedgerHandler(
    IPortfolioLedgerStore ledger,
    ILogger<OrderApprovedLedgerHandler> logger)
{
    public void Handle(OrderApproved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // 冪等（同一 DecisionId の再送は無視）はストア側で担保する。
        ledger.AppendApproval(message.DecisionId, message.Intent, message.ApprovedAt);
        logger.LogDebug(
            "台帳に承認を記録: DecisionId={DecisionId} 銘柄={Symbol} 効果={Effect}",
            message.DecisionId, message.Intent.Symbol, message.Intent.PositionEffect);
    }
}
