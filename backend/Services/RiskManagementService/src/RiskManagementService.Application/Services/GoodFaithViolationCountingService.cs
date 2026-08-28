using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.Services;

// FR-19, FR-10, FR-11, UC-06, #425, ADR-0025 決定2, IADR-0165:
// **GFV 発生回数の自前計数。** 約定 1 件について「未決済資金による買付だったか」を事後に判定し、
// 該当すれば追記台帳へ 1 件記録して監査イベントを返す。
//
// ★★ 何を数えているのかを取り違えないこと（ADR-0025 §理由 が明記した限界） ★★
//
//   自前で数えられるのは**自らのガードをすり抜けた買付**だけであり、**ブローカー側が独自に GFV と判定した
//   事象は捕捉できない**。したがって本計数は「ブローカーの GFV カウンタの写し」ではなく
//   「**自らのガードの失敗回数**」である。**両者が一致する保証はない。**
//   **ガードが正しく働けば発生回数は 0 のままであり、記録されるのはガードをすり抜けた場合だけである。**
//
// 判定そのものは純関数（GoodFaithViolationDetection）が持ち、本サービスは
// 「入力の収集 → 判定 → 追記 → 発行するイベント」の束ねに徹する（BuyInInferenceService と同じ規律）。
public sealed class GoodFaithViolationCountingService(
    IPortfolioLedgerStore ledger,
    IBrokerAccountObservationStore accountObservations,
    IGoodFaithViolationStore store,
    IClock clock)
{
    /// <summary>
    /// 約定 1 件を検査し、GFV 発生と判定したら台帳へ追記して発行すべきイベントを返す。該当しなければ <c>null</c>。
    /// </summary>
    /// <param name="execution">約定イベント。</param>
    /// <param name="occurredOn">
    /// 計上する取引日（**米国東部時間**）。変換は供給側（Infrastructure の <c>EasternTradingDate</c>）が
    /// 1 か所で行う規律（IADR-0137 決定1）に従い、本サービスはタイムゾーンを解釈しない。
    /// </param>
    public GoodFaithViolationRecorded? Observe(OrderExecuted execution, DateOnly occurredOn)
    {
        ArgumentNullException.ThrowIfNull(execution);

        // 約定していない結果（受付・失注・約定 0 の取消・拒否）は「買付」ではない。
        if (execution.FilledQuantity <= 0)
        {
            return null;
        }

        // OrderExecuted は売買方向・建玉効果・換算レートを運ばない。承認台帳（DecisionId）から引く。
        // **引けなければ記録しない**——金額も方向も分からない約定を推測で違反として記録しない。
        if (ledger.FindApprovedIntent(execution.DecisionId) is not { } intent)
        {
            return null;
        }

        // 口座種別は**現在有効な観測**（IADR-0153 決定3・有効期間 30 分）を用いる。
        // **観測が無い／信用口座なら記録しない**（GoodFaithViolationDetection の判定に委ねる）。
        var account = accountObservations.GetCurrent();

        // 金額は**本約定 1 件ぶん**（基準通貨建て）。発注前のガードが「当日の新規建て累計 ＋ 本注文」で
        // 判定する（IADR-0153 決定4）のとは粒度が違う。**決済済み資金が供給されていない現状では、
        // 述語の第 1 項（未供給）で真になるため差は生じない。** PoC 項目 8（ADR-0019・ADR-0025 決定1）が
        // 成立して決済済み資金が供給された時点で、累計での比較へ揃えること（IADR-0165 §フォローアップ）。
        var purchaseAmountInBase = execution.FilledQuantity * execution.AveragePrice * intent.FxRateToBase;

        if (!GoodFaithViolationDetection.CountsAsViolation(
                account, intent.Side, intent.PositionEffect, purchaseAmountInBase))
        {
            return null;
        }

        var id = Guid.NewGuid();
        var recordedAt = clock.UtcNow;

        // **記録が先、発行が後。** 逆順にすると「発行できたが記録が落ちた」違反が生まれ、件数が過小になる
        // （緩い側。IADR-0148 決定3 と同じ順序の規律）。追記は OrderId で冪等なので再送で壊れない。
        store.Append(new GoodFaithViolationRecord(
            id,
            execution.OrderId,
            execution.DecisionId,
            intent.Symbol,
            intent.Market,
            purchaseAmountInBase,
            account!.SettledCashInBase,
            occurredOn,
            execution.ExecutedAt,
            recordedAt));

        return new GoodFaithViolationRecorded(
            id,
            execution.DecisionId,
            execution.OrderId,
            intent.Symbol,
            intent.Market,
            purchaseAmountInBase,
            account.SettledCashInBase,
            occurredOn,
            execution.ExecutedAt,
            recordedAt);
    }
}
