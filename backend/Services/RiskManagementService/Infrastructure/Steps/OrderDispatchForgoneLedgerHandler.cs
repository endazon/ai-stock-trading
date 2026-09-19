using RiskManagementService.Domain;
using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace RiskManagementService.Infrastructure.Steps;

// 🔴 FR-05, FR-10, FR-11, UC-06, #852, IADR-0356, IADR-0117（2026-09-19 追記）, IADR-0211:
// 発注執行が**見送った**承認（ブローカーへ発注していない）を取引台帳へ届け、「処理中の決済」から外す。
//
// #848 が塞いだのは「**終端**になった承認」（取消・失効・拒否）であり、見送りは終端ではない
// ——そもそも注文が存在しないため注文状態を持たない（IADR-0211）——ので対象外だった。その結果、
// OpenD の再起動中（ADR-0002 の SPOF・ADR-0024）に見送られた手仕舞いは、窓（既定 30 分）が満了するまで
// 建玉をロックし続けた。**見送りは手仕舞いが必要なときにまとまって出る**ため実害が大きい（#852）。
//
// 🔴 **理由を見ずに一律で外さない。** 外してよいのは見送りが「**確実に未発注**」を意味するときだけで、
// その判定は `OrderDispatchForgoneLifecycle.ConfirmsNoOrderPlaced`（allowlist・既定は解放しない）が持つ。
// 「送ったかもしれない見送り」で押さえを解くと、証券会社側で生きているかもしれない手仕舞いと合わせて
// 同じ株数に 2 本の決済が並ぶ＝**二重決済でショート化**する。
//
// ADR-0013, IADR-0129 決定 10: 本ハンドラは `OrderDispatchForgoneActivityHandler`（注文アクティビティ射影）と
// **同一のハンドラチェーン**で実行される（Wolverine では 1 サービス内 1 イベント型 = 1 キュー）。
// **新しいキューは増えない。** 再試行では両方が再実行されるが、双方の書き込みは冪等である
// —— `MarkForgone` は単調（既に終端／見送りなら何もしない）、`RecordForgone` はストア側で冪等。
//
// 台帳に相関する承認が無い見送り（承認より先に届いた・台帳に載らない注文）は `MarkForgone` 側が無視する
//（後着の承認は終端未確認＝処理中として数える。安全側へ倒れる）。
public sealed class OrderDispatchForgoneLedgerHandler(
    IPortfolioLedgerStore ledger,
    ILogger<OrderDispatchForgoneLedgerHandler> logger)
{
    public void Handle(OrderDispatchForgone message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!OrderDispatchForgoneLifecycle.ConfirmsNoOrderPlaced(message.Reason))
        {
            // 🔴 fail-safe: 分類されていない理由では在庫を解放しない。**沈黙させない**——
            // 新しい理由が足されたのに分類されていないことは、ここのログでしか外から見えない。
            logger.LogWarning(
                "見送りの理由が「確実に未発注」と分類されていないため、取引台帳の押さえを解かない: "
                + "DecisionId={DecisionId} 理由={Reason} 銘柄={Symbol} 効果={Effect}",
                message.DecisionId, message.Reason, message.Intent.Symbol, message.Intent.PositionEffect);
            return;
        }

        ledger.MarkForgone(message.DecisionId, message.OccurredAt);
        logger.LogDebug(
            "台帳に見送りを記録（処理中から外す）: DecisionId={DecisionId} 理由={Reason} 銘柄={Symbol} 効果={Effect}",
            message.DecisionId, message.Reason, message.Intent.Symbol, message.Intent.PositionEffect);
    }
}
