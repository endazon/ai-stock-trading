using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// FR-05, UC-01, UC-02, ADR-0002/0003: 承認済み注文（OrderApproved）をブローカへ発注し、
// 結果（約定/拒否/取消）を OrderExecuted として確定する。注文実体＋スリッページを永続化する（FR-16 の月報データ源）。
// ブローカ実装は IBrokerAdapter で差し替える（既定はペーパー・moomoo は SIMULATE 限定。IADR-0016/0056）。
//
// #131, IADR-0057: 発注は「予約 → 発注 → 確定」の3相で冪等化する。ブローカ発注の前に DecisionId の一意予約を
// コミットすることで、「ブローカ発注成功 → 永続化失敗」の窓でも再配送時に二重発注しない。
//
// FR-10, #331, IADR-0210: 損切りはブローカー側逆指値へ一本化した。エントリー（Open）には保護逆指値を
// **同時発注**し、逆指値を張れない Open では建玉を持たない（見送り／取消／成行手仕舞い＝fail-closed）。
// FR-05, #331, IADR-0211: OpenD へ確実に届いていない発注（BrokerUnavailableException）は Rejected へ丸めず、
// 予約を解放して「見送り」（OrderDispatchForgone）で正常終了する（キューイングしない）。
//
// FR-10, FR-12, ADR-0040 決定1, #819, IADR-0342: 損切りの実行機構は承認が運ぶ（既定 S0）。解釈は
// StopLossMethodPolicy だけが行う。**S2（moomoo SIMULATE の新規買いに限る）では保護逆指値を発注せず建玉を保持し、
// 免除の事実（ProtectiveStopWaived）を発行する**——「逆指値なしの建玉を持たない」の例外はこの 1 分岐だけである。
// S0 以外が SIMULATE 以外へ届いたら発注しない（実弾を無防備にしない）。S1 は未実装のため S0 と同じ扱い。
//
// FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: **S3 は保護レグを代替注文種別（StopLimit / TrailingStop）で
// 発注し、種別と拒否理由（retType / retMsg）を AlternativeProtectiveStopAttempted として残す**。
// 結果の扱いは S0 と完全に同じ（受理＝保護レグの記録／拒否＝建玉を持たない）——分岐するのは「何で発注するか」だけである。
public sealed class OrderExecutionAppService(
    IBrokerAdapter broker,
    IExecutedOrderStore store,
    IOrderReservationStore reservations,
    IClock clock,
    IProtectiveStopOrderStore? protectiveStops = null,
    ILogger<OrderExecutionAppService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<OrderExecutionAppService>.Instance;

    public async Task<OrderDispatchResult> ExecuteAsync(OrderApproved approved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approved);

        // 相1（完了の権威）: 同一 DecisionId の発注結果が既にあれば、再発注せず既存結果を再発行する。メッセージングの
        // 再配送（UseAiStockTradingRabbitMq の共通再試行）で同一 OrderApproved が再処理されても二重発注・二重計上しない。DecisionId は
        // 取引判断/機械執行1件に対応する。
        // 予約表（相2）の導入前に既に存在する行にも効かせるため、この照合を完了判定の権威として残す（IADR-0057）。
        // 保護レグのイベントは再発行しない——逆指値レグは初回処理で記録済みであり、台帳の承認行（AppendApproval）も
        // DecisionId で冪等のため、再発行しなくても失われない。
        var existing = store.FindByDecisionId(approved.DecisionId);
        if (existing is not null)
        {
            // FR-20, #386, IADR-0149 決定1: 発注先は**現在のアダプタ**の値である。記録（ExecutionRecord）は
            // 発注先を保持しないため、再発行の時点で構成が変わっていれば当時と異なる値が載り得る。
            // 下流（Stage 1 の取引件数）は DecisionId で先着優先に記録するため、既に観測済みの注文は
            // 上書きされない。残余リスクは IADR-0149 に記録した。
            // NFR-01, NFR-02, #689, IADR-0307: 再発行でも起点は**今届いた承認**が運ぶものを載せる
            // （記録（ExecutionRecord）は起点を保持しない）。載せなければ記録完了の区間が閉じない。
            return OrderDispatchResult.FromExecuted(new OrderExecuted(
                existing.DecisionId, existing.OrderId, existing.Status,
                existing.FilledQuantity, existing.AveragePrice, existing.ExecutedAt, broker.Provider,
                approved.CycleTrigger, approved.CycleStartedAt));
        }

        var intent = approved.Intent;

        // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4: 承認が運ぶ手法を解決する（Open にのみ効く）。
        // S0 は常に S0 であり、以降の分岐は 1 バイトも変わらない。
        var disposition = intent.PositionEffect == PositionEffect.Open
            ? ResolveStopLossMethod(approved)
            : StopLossMethodDisposition.BrokerStopOrder;
        if (disposition == StopLossMethodDisposition.Refused)
        {
            return Forgone(approved, OrderDispatchForgoneReason.StopLossMethodNotPermitted);
        }

        // FR-10, #331, IADR-0210 決定1: 逆指値を張れない Open は**発注せず**見送る（建玉を作らない側へ倒す）。
        // 予約の前に判定する（発注に着手しないため予約は要らない）。
        if (intent.PositionEffect == PositionEffect.Open)
        {
            if (intent.StopLossPrice is not { } stopLoss || stopLoss <= 0m)
            {
                return Forgone(approved, OrderDispatchForgoneReason.StopLossPriceMissing);
            }

            if (broker is not IProtectiveOrderBroker)
            {
                return Forgone(approved, OrderDispatchForgoneReason.StopOrderUnsupported);
            }

            // FR-10, ADR-0040 決定1（S3）, #821, IADR-0347: S3 の能力が無い発注先へ S3 が届いたら**発注しない**
            // （S0 へ黙って読み替えない。上の「逆指値能力が無い Open は見送る」と同じ fail-closed）。
            if (disposition == StopLossMethodDisposition.AlternativeBrokerOrderType
                && broker is not IAlternativeProtectiveOrderBroker)
            {
                return Forgone(approved, OrderDispatchForgoneReason.StopOrderUnsupported);
            }
        }

        // 相2（発注着手の権威）: ブローカへ送る「前」に一意予約を確保する。確保できない＝予約済みで未確定であり、
        // 「未発注」と「発注済みだが記録できていない」を区別できない。実弾では二重発注（不可逆）の方が
        // 取りこぼし（可逆）より重いため、再発注せず拒否する（at-most-once・IADR-0057）。
        // 再試行を使い切ると _error キューへ送られ、ブローカ状態を確認するリコンサイルの対象になる。
        if (!reservations.TryReserve(approved.DecisionId, clock.UtcNow))
            throw new OrderDispatchReservationConflictException(approved.DecisionId);

        // 相3: ADR-0003: 承認済み注文のみ発注する。Close（owner 手仕舞い・自動縮小）も同一経路。
        // #141, IADR-0092: ブローカが client order id 伝播に対応していれば DecisionId を紐づけて発注する
        // （滞留 Reserved を後から DecisionId で照合＝実照会リコンサイルの前提）。非対応（paper 等）は従来経路。
        BrokerOrder brokerOrder;
        try
        {
            brokerOrder = broker is IClientOrderIdBroker correlating
                ? await correlating.PlaceOrderAsync(intent, approved.DecisionId, cancellationToken).ConfigureAwait(false)
                : await broker.PlaceOrderAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            // FR-05, ADR-0002（SPOF・再起動中は発注不可）, #331, IADR-0211: 接続確立の失敗＝**確実に未発注**。
            // 予約を解放し（二重発注の窓は無い）、キューイングせず見送りで正常終了する（Rejected へ丸めない）。
            // 送信後の失敗（届いたか不明）は本例外の契約外であり、BrokerDispatchIndeterminateException として
            // 伝播する（次の catch。予約を解放せず据え置き、リコンサイルが守る。#848・IADR-0117 改定 6）。
            reservations.Release(approved.DecisionId);
            return Forgone(approved, OrderDispatchForgoneReason.BrokerUnavailable);
        }
        catch (BrokerDispatchIndeterminateException ex)
        {
            // 🔴 FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）:
            // **送信後に結果を確認できなかった＝届いたか不明。** ここで行ってよいことは「何もしない」だけである。
            //   - 予約を**解放しない**（解放すると再配送で二重発注になる。BrokerUnavailable との決定的な違い）。
            //   - 予約を**確定しない**・結果を**保存しない**（実在しない注文 ID の終端記録を台帳へ残さない）。
            //   - 見送り（OrderDispatchForgone）にも**しない**——見送りは「発注していない」という主張であり、
            //     ここでそれを主張すると Rejected と同じ誤り（建玉が無いという仮定）になる。
            // 予約は Reserved のまま残り、client order id によるリコンサイル（IADR-0092 / IADR-0074）が
            // 実状態を Placed / NotPlaced / Indeterminate に解決する。**本経路は例外で終わるのが正しい。**
            _logger.LogError(ex,
                "発注の結果を確認できませんでした（送信済み・届いたか不明）: DecisionId={DecisionId} 銘柄={Symbol} 数量={Quantity}。"
                + "予約は Reserved のまま据え置き、リコンサイルの解決に委ねます（拒否へ畳まず・見送りにもしません）。",
                approved.DecisionId, intent.Symbol, intent.Quantity);
            throw;
        }

        // FR-16: 実効スリッページを取引毎に算出・記録する。
        var slippage = SlippageCalculator.Compute(intent.Price, brokerOrder.AveragePrice, intent.Side);

        // 約定時刻はブローカ往復の「後」に取る。予約時刻を流用すると往復の実時間が記録から消え、
        // 監査・リコンサイル時に「予約時刻＝約定時刻」に見えてしまう。
        var now = clock.UtcNow;

        // 相4（確定）: 結果を保存してから予約を確定する。この順序により、Save 成功・確定失敗で落ちても
        // 再処理は相1で既存結果を再発行できる（逆順だと結果の無い Completed 予約が生じ、窓が残る）。
        store.Save(new ExecutionRecord(
            approved.DecisionId,
            brokerOrder.OrderId,
            intent.Symbol,
            intent.Market,
            intent.Side,
            intent.ProductType,
            intent.PositionEffect,
            intent.Quantity,
            intent.Price,
            brokerOrder.FilledQuantity,
            brokerOrder.AveragePrice,
            brokerOrder.Status,
            slippage,
            now));

        reservations.MarkCompleted(approved.DecisionId, brokerOrder.OrderId, now);

        // FR-20, FR-12, #386, IADR-0149 決定1: **実際に発注したアダプタの発注先**を載せる。
        // 取引判断が運ぶ intent.Mode は「段階が定める既定の発注先」であって現在の発注先ではない（IADR-0140 決定3）。
        // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点を記録側（監査）まで運ぶ。
        var executed = new OrderExecuted(
            approved.DecisionId,
            brokerOrder.OrderId,
            brokerOrder.Status,
            brokerOrder.FilledQuantity,
            brokerOrder.AveragePrice,
            now,
            broker.Provider,
            approved.CycleTrigger,
            approved.CycleStartedAt);

        // FR-10, #331, IADR-0210 決定1/3: Open のエントリーが生きている（＝建玉になった・なり得る:
        // Accepted / PartiallyFilled / Filled）なら、保護逆指値を**同時発注**する。
        // 未受理なら建玉を持たない（未約定→取消／約定済み→成行手仕舞い）。
        // 終端失敗（Rejected / Cancelled / Expired）は建玉が生じないため保護レグ自体が不要である。
        var entryAlive = brokerOrder.Status
            is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled;
        if (intent.PositionEffect == PositionEffect.Open && entryAlive
            && disposition == StopLossMethodDisposition.ProtectiveStopWaived)
        {
            // FR-10, FR-12, ADR-0040 決定1（S2）, #819, IADR-0342 決定6: 保護逆指値を発注せず建玉を保持する。
            // 取消・手仕舞いも行わない。逆指値レグの記録（protective_stop_orders）を作らないため、
            // ProtectiveStopGuard の巡回対象に入らない（失効扱いで手仕舞われることが構造的に無い）。
            var waived = new ProtectiveStopWaived(
                approved.DecisionId, intent.Symbol, intent.Market, intent.Side, intent.ProductType,
                intent.Quantity, intent.StopLossPrice, approved.StopLossMethod, broker.Provider, now);
            return OrderDispatchResult.FromExecuted(executed, stopWaived: waived);
        }

        if (intent.PositionEffect == PositionEffect.Open && entryAlive)
        {
            var useAlternative = disposition == StopLossMethodDisposition.AlternativeBrokerOrderType;
            var (stopPlaced, coverageLost, attempted) = await PlaceProtectiveStopAsync(
                    approved, brokerOrder, useAlternative, cancellationToken)
                .ConfigureAwait(false);
            return OrderDispatchResult.FromExecuted(executed, stopPlaced, coverageLost, stopAttempted: attempted);
        }

        return OrderDispatchResult.FromExecuted(executed);
    }

    // FR-10, ADR-0040 決定1, #819, IADR-0342 決定4・決定7: 解決とログ。拒否は Error（実弾で S0 以外が
    // 有効＝設定側の関門が 2 方向とも破られた状態であり、放置してはならない）、未実装は Warning。
    private StopLossMethodDisposition ResolveStopLossMethod(OrderApproved approved)
    {
        var disposition = StopLossMethodPolicy.Resolve(approved.StopLossMethod, approved.Intent, broker.Provider);
        switch (disposition)
        {
            case StopLossMethodDisposition.Refused:
                _logger.LogError(
                    "損切りの実行機構 {Method} は moomoo SIMULATE でしか選べませんが、発注先は {Provider} です。"
                    + "発注しません（DecisionId={DecisionId}・ADR-0040 決定1）。利用者の設定で S0 へ戻してください。",
                    approved.StopLossMethod, broker.Provider, approved.DecisionId);
                break;
            case StopLossMethodDisposition.NotImplementedFallbackToBrokerStop:
                _logger.LogWarning(
                    "損切りの実行機構 {Method} は未実装のため S0（ブローカー側逆指値）と同じ扱いで発注します"
                    + "（DecisionId={DecisionId}・S1=#820）。",
                    approved.StopLossMethod, approved.DecisionId);
                break;
        }

        return disposition;
    }

    private OrderDispatchResult Forgone(OrderApproved approved, OrderDispatchForgoneReason reason) =>
        OrderDispatchResult.FromForgone(
            new OrderDispatchForgone(approved.DecisionId, approved.Intent, reason, clock.UtcNow));

    // FR-10, UC-02, #331, IADR-0210 決定1/3: 保護逆指値の同時発注と、未受理時の建玉解消の全分岐。
    // FR-10, #821, IADR-0347: useAlternative（S3）のときだけ代替注文種別で発注し、試行の記録を返す。
    // **未受理・受理の後段の扱いは分岐しない**（S0 と同じ 1 本の経路）。
    private async Task<(ProtectiveStopPlaced? StopPlaced, ProtectiveStopCoverageLost? CoverageLost,
        AlternativeProtectiveStopAttempted? StopAttempted)>
        PlaceProtectiveStopAsync(
            OrderApproved approved, BrokerOrder entryOrder, bool useAlternative, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        var protective = (IProtectiveOrderBroker)broker; // 事前検証済み（未実装なら見送りで到達しない）
        var triggerPrice = intent.StopLossPrice!.Value;   // 事前検証済み（null/非正なら見送りで到達しない）
        const int attempt = 1;

        var stopDecisionId = ProtectiveStopIds.StopDecisionId(approved.DecisionId, attempt);
        var closeIntent = BuildCloseIntent(intent, intent.Quantity, triggerPrice);

        BrokerOrder? stopOrder = null;
        AlternativeProtectiveStopAttempted? attempted = null;
        try
        {
            if (useAlternative)
            {
                // 事前検証済み（能力が無ければ見送りで到達しない）。
                var alternative = (IAlternativeProtectiveOrderBroker)broker;
                var placement = await alternative
                    .PlaceAlternativeStopOrderAsync(
                        closeIntent, triggerPrice, intent.Price, stopDecisionId, cancellationToken)
                    .ConfigureAwait(false);
                stopOrder = placement.Order;
                attempted = Attempted(
                    approved, stopDecisionId, placement.OrderType, placement.Order.Status,
                    placement.Order.OrderId, placement.RejectReasonCode, placement.RejectReasonMessage);
            }
            else
            {
                stopOrder = await protective
                    .PlaceStopOrderAsync(closeIntent, triggerPrice, stopDecisionId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 逆指値の発注失敗（接続断含む）＝未受理と同じ分岐（建玉を持たない）。原因は解消側の結果に現れる。
            stopOrder = null;
            if (useAlternative)
            {
                // #821, IADR-0347: 例外で落ちても「何の種別で試したか」は残す（種別は発注前に知れる）。
                attempted = Attempted(
                    approved, stopDecisionId, ((IAlternativeProtectiveOrderBroker)broker).AlternativeProtectiveOrderType,
                    OrderStatus.Rejected, brokerOrderId: null, rejectReasonCode: null, rejectReasonMessage: ex.Message);
            }
        }

        var now = clock.UtcNow;

        if (stopOrder is not null && stopOrder.Status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
        {
            // 受理: 逆指値レグを ExecutionRecord として保存し、既存の約定追跡ポーリング（IADR-0113）に載せる。
            // 逆指値がブローカー側で約定（＝損切り成立）すると OrderExecuted が台帳の建玉を減らす（IADR-0210 決定2）。
            store.Save(new ExecutionRecord(
                stopDecisionId, stopOrder.OrderId, intent.Symbol, intent.Market, closeIntent.Side,
                intent.ProductType, PositionEffect.Close, intent.Quantity, triggerPrice,
                stopOrder.FilledQuantity, stopOrder.AveragePrice, stopOrder.Status,
                SlippageRatio: 0m, now));

            protectiveStops?.Save(new ProtectiveStopOrder(
                approved.DecisionId, stopDecisionId, stopOrder.OrderId, intent.Symbol, intent.Market,
                intent.Side, intent.ProductType, intent.Mode, intent.Quantity, triggerPrice,
                intent.FxRateToBase, attempt, ProtectiveStopState.Active, now, now));

            return (new ProtectiveStopPlaced(
                    approved.DecisionId, stopDecisionId, stopOrder.OrderId, closeIntent, triggerPrice, attempt, now),
                null, attempted);
        }

        // 未受理: 逆指値なしの建玉を持たない（業務フロー 02 の表）。
        var coverageLost = await ResolveUnprotectedEntryAsync(approved, entryOrder, cancellationToken)
            .ConfigureAwait(false);
        return (null, coverageLost, attempted);
    }

    // FR-10, FR-11, FR-12, #821, IADR-0347: S3 の試行の記録（受理・拒否のどちらでも 1 件）。
    private AlternativeProtectiveStopAttempted Attempted(
        OrderApproved approved,
        Guid stopDecisionId,
        AlternativeProtectiveOrderType orderType,
        OrderStatus status,
        string? brokerOrderId,
        int? rejectReasonCode,
        string? rejectReasonMessage) =>
        new(approved.DecisionId, stopDecisionId, approved.Intent.Symbol, approved.Intent.Market,
            orderType, status, brokerOrderId, rejectReasonCode, rejectReasonMessage,
            approved.StopLossMethod, broker.Provider, clock.UtcNow);

    // 未受理時の建玉解消: 未約定なら取消、約定済みなら成行手仕舞い、いずれも失敗なら None（Critical・人手対応）。
    private async Task<ProtectiveStopCoverageLost> ResolveUnprotectedEntryAsync(
        OrderApproved approved, BrokerOrder entryOrder, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        var filled = entryOrder.FilledQuantity;

        if (filled == 0)
        {
            try
            {
                await broker.CancelOrderAsync(entryOrder.OrderId, cancellationToken).ConfigureAwait(false);
                return CoverageLost(approved, ProtectiveStopRemediation.EntryCancelled, intent.Quantity);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消失敗＝その間に約定した可能性。ブローカ状態を照会して約定分を手仕舞いへ回す。
                var snapshot = await TryGetOrderAsync(entryOrder.OrderId, cancellationToken).ConfigureAwait(false);
                filled = snapshot?.FilledQuantity ?? 0;
                if (filled == 0)
                {
                    // 取消も照会もできない: 状態不明のまま自動で注文を重ねない（人手対応・Critical）。
                    return CoverageLost(approved, ProtectiveStopRemediation.None, intent.Quantity);
                }
            }
        }

        return await CloseUnprotectedPositionAsync(approved, filled, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProtectiveStopCoverageLost> CloseUnprotectedPositionAsync(
        OrderApproved approved, int quantity, CancellationToken cancellationToken)
    {
        var intent = approved.Intent;
        var protective = (IProtectiveOrderBroker)broker;
        var closeDecisionId = ProtectiveStopIds.CloseDecisionId(approved.DecisionId, attempt: 1);
        // 参照価格は判断時点の価格（intent.Price）。成行手仕舞いの実約定はブローカ側で決まる。
        var closeIntent = BuildCloseIntent(intent, quantity, intent.Price);

        try
        {
            var closeOrder = await protective
                .PlaceMarketOrderAsync(closeIntent, closeDecisionId, cancellationToken)
                .ConfigureAwait(false);
            var now = clock.UtcNow;

            // 手仕舞いレグも ExecutionRecord に載せ、約定追跡・台帳反映を既存経路で行う（IADR-0210 決定3）。
            store.Save(new ExecutionRecord(
                closeDecisionId, closeOrder.OrderId, intent.Symbol, intent.Market, closeIntent.Side,
                intent.ProductType, PositionEffect.Close, quantity, closeIntent.Price,
                closeOrder.FilledQuantity, closeOrder.AveragePrice, closeOrder.Status,
                SlippageCalculator.Compute(closeIntent.Price, closeOrder.AveragePrice, closeIntent.Side), now));

            return new ProtectiveStopCoverageLost(
                approved.DecisionId, intent.Symbol, intent.Market,
                ProtectiveStopLossCause.RejectedAtEntry, ProtectiveStopRemediation.PositionClosed,
                quantity, closeDecisionId, closeIntent, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CoverageLost(approved, ProtectiveStopRemediation.None, quantity);
        }
    }

    private ProtectiveStopCoverageLost CoverageLost(
        OrderApproved approved, ProtectiveStopRemediation remediation, int quantity) =>
        new(approved.DecisionId, approved.Intent.Symbol, approved.Intent.Market,
            ProtectiveStopLossCause.RejectedAtEntry, remediation, quantity,
            CloseDecisionId: null, CloseIntent: null, clock.UtcNow);

    private async Task<BrokerOrder?> TryGetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        try
        {
            return await broker.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // FR-17, IADR-0107: 決済レグはエントリーの換算レート（FxRateToBase）を必ず引き継ぐ。
    // 落とすと外貨建て決済だけが未換算（レート 1）で台帳へ積まれ、実現損益の基準通貨集計が桁で誤る。
    private static OrderIntent BuildCloseIntent(OrderIntent entry, int quantity, decimal referencePrice) =>
        new(entry.Symbol,
            entry.Market,
            entry.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy,
            entry.ProductType,
            entry.Mode,
            quantity,
            referencePrice,
            PositionEffect.Close,
            StopLossPrice: null,
            entry.FxRateToBase);
}
