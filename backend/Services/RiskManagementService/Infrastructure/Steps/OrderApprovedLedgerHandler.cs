using RiskManagementService.Features.RiskManagement;
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
//
// FR-06, FR-16, #611, IADR-0285 決定1: 承認記録の直前に**認識時レート**（1 USD あたりの円）を解決し、承認行へ固定する。
// 承認は取引判断の直後・約定の直前であり、IADR-0107 決定2（承認時点のレート＝約定時レートの近似）と同じ時点である。
// 解決できなければ null（未記録）のまま記録する——**承認記録を為替解決の失敗で止めない**（解決器が fail-safe を担う）。
public sealed class OrderApprovedLedgerHandler(
    IPortfolioLedgerStore ledger,
    IRecognitionFxRateResolver recognitionFxRate,
    ILogger<OrderApprovedLedgerHandler> logger)
{
    public async Task Handle(OrderApproved message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var fxRateBaseToDisplay = await recognitionFxRate
            .ResolveBaseToDisplayAsync(cancellationToken).ConfigureAwait(false);

        // 冪等（同一 DecisionId の再送は無視）はストア側で担保する。
        ledger.AppendApproval(message.DecisionId, message.Intent, message.ApprovedAt, fxRateBaseToDisplay);
        logger.LogDebug(
            "台帳に承認を記録: DecisionId={DecisionId} 銘柄={Symbol} 効果={Effect} 認識時レート(JPY/USD)={FxRateBaseToDisplay}",
            message.DecisionId, message.Intent.Symbol, message.Intent.PositionEffect, fxRateBaseToDisplay);
    }
}
