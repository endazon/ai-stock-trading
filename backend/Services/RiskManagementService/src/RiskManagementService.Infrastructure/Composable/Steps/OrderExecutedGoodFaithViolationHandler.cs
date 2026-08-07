using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0166:
// 約定（OrderExecuted）を購読し、**未決済資金による買付＝GFV 発生**を事後に計数する。
//
// ★★ 何を数えているのかを取り違えないこと（ADR-0025 §理由 が明記した限界） ★★
//
//   自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、**ブローカー側が独自に GFV と判定した
//   事象は捕捉できない**。したがって本計数は「ブローカーの GFV カウンタの写し」ではなく
//   「**自らのガードの失敗回数**」である。**両者が一致する保証はない。**
//
// **ガードが正しく働けば本ハンドラは 1 件も記録しない。** 記録が出た時点で、発注前の GFV 回避ガード
// （CashAccountSettlementHold）をすり抜けた買付が現に約定したということであり、ガードの不具合または
// 口座観測の欠落を示す。だからこそ監査ログ（FR-11）に残す——ADR-0025 が手入力を採らなかった理由の 1 つが
// 「監査証跡に乗らない」ことであった。
//
// ADR-0013, IADR-0129 決定10, #354: 本ハンドラは OrderExecutedLedgerHandler / OrderExecutedActivityHandler /
// OrderExecutedStage1FillHandler と同一のハンドラチェーンで実行され、再試行では全部が再実行される。
// 本ハンドラの書き込みは **OrderId で先着優先の冪等**であり、再実行でも件数が増えない。
public sealed class OrderExecutedGoodFaithViolationHandler(
    GoodFaithViolationCountingService counting,
    ILogger<OrderExecutedGoodFaithViolationHandler> logger)
{
    public async Task Handle(OrderExecuted message, IMessageBus bus)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        // 取引日（米国東部時間）への変換は供給側の 1 か所に閉じる（IADR-0137 決定1・EasternTradingDate）。
        var recorded = counting.Observe(message, EasternTradingDate.From(message.ExecutedAt));
        if (recorded is null)
        {
            return;
        }

        // **警告として出す。** 通常運行では 1 件も出ない事象であり、出た時点でガードの失敗である。
        logger.LogWarning(
            "未決済資金による買付を検出しGFV発生として計数しました（自らのガードの失敗であり、"
            + "ブローカーのGFV判定とは一致しない）: OrderId={OrderId} DecisionId={DecisionId} "
            + "銘柄={Symbol}/{Market} 金額={Amount} 決済済み資金={SettledCash}",
            recorded.OrderId, recorded.DecisionId, recorded.Symbol, recorded.Market,
            recorded.PurchaseAmountInBase,
            recorded.SettledCashInBase?.ToString() ?? "未供給");

        // 監査台帳（FR-11）へ運ぶ。**記録（台帳への追記）は既に済んでいる**——逆順にすると
        // 「発行できたが記録が落ちた」違反が生まれ、件数が過小になる（緩い側）。
        await bus.PublishAsync(recorded).ConfigureAwait(false);
    }
}
