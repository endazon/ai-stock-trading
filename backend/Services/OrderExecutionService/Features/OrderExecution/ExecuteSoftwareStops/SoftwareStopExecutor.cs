using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820, IADR-0344 決定4・決定5: ソフトウェア逆指値の発動。
// 市場監視の損切りライン到達（StopLossTriggered）を受けて、到達したソフトウェア逆指値を**成行で**決済する。
// 同じ決済処理（TryCloseAsync）を常駐ガード（ProtectiveStopGuard）が到達済みの行の再試行に使う。
//
// 🔴 **決済数量は「その行の残保護数量」そのものである**（IADR-0344 追記(4)）。建玉の純額から毎回持ち分を計算し直すのを
// やめ、記録が自分の残保護数量を状態として持つ（確定 → 自分の決済で減算 → 外部要因の減少を一度だけ割り当て）。
// 割り当てと確定は ProtectiveStopNetting.ReconcileShares が行い、**保存して記録する**ため巡回ごとに揺れない。
//
// 🔴 **二重決済を作らない**（ADR-0040 §結果「S1 は二重決済の経路を SIMULATE に戻す」）:
//   (i)   決済レグの DecisionId は (エントリー, 試行番号) から決定的に導出し、同じ DecisionId の発注記録があれば再送しない。
//   (ii)  送る前に予約表（IADR-0057 の一意制約）で DecisionId を確保し、確保できなければ送らない（ハンドラとガードの並行・再配送）。
//   (iii) 受理した数量だけ残保護数量を減らし、**0 になった行だけ Completed にする**（以後は突き合わせの候補に入らない）。
//
// fail-safe（据え置き＝イベントなし・到達の記録は残す）:
//   - 建玉照会の null（不明）・接続断・取消した未約定エントリーがまだ終端でない → 次回（ガードの巡回 or 次の到達）で再試行する。
//   - 「不明」を「建玉なし」と取り違えて行を完了させない（IADR-0118 と同じ規律）。
//   - **据え置きが続く行は猶予を過ぎたら Critical を 1 回出す**（無音の失敗を残さない。IADR-0344 追記(4) 決定9）。
public sealed class SoftwareStopExecutor(
    IBrokerAdapter broker,
    IBrokerPositionSource? positions,
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IOrderReservationStore reservations,
    IClock clock,
    ILogger<SoftwareStopExecutor>? logger = null,
    TimeSpan? orphanGrace = null,
    TimeSpan? settlementGrace = null)
{
    /// <summary>到達 1 回あたりの決済の試行上限（拒否が続いたら打ち切って Critical を出す。IADR-0344 決定5-6）。</summary>
    public const int MaxCloseAttemptsPerTrigger = 3;

    /// <summary>
    /// #820 の監査, IADR-0344 決定5-7: エントリーの発注記録が見つからない行を「孤立」と断じるまでの猶予。
    /// 発注直後に記録だけが遅れている場合（送信と記録のあいだのクラッシュ窓・リコンサイル待ち）を殺さない長さにする。
    /// </summary>
    public static readonly TimeSpan DefaultOrphanGrace = TimeSpan.FromMinutes(15);

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4) 決定9: <b>到達済みなのに決済できない</b>状態を Critical で知らせるまでの猶予。
    /// 据え置き自体は正しい fail-safe だが、無期限に黙って続くと「損切りが出ていない」ことに誰も気づかない。
    /// </summary>
    public static readonly TimeSpan DefaultSettlementGrace = TimeSpan.FromMinutes(15);

    // 持ち分の確定・割り当て（IADR-0344 追記(4)）で参照する Active 行の上限。保有建玉数上限（既定 3）に対して十分大きい。
    private const int NettingScanLimit = 500;

    private readonly ILogger _logger = logger ?? NullLogger<SoftwareStopExecutor>.Instance;

    private readonly TimeSpan _orphanGrace = orphanGrace ?? DefaultOrphanGrace;

    private readonly TimeSpan _settlementGrace = settlementGrace ?? DefaultSettlementGrace;

    /// <summary>
    /// 損切りライン到達を受けて、該当するソフトウェア逆指値を決済する。発行すべきイベントを返す（発行は呼び出し側）。
    /// </summary>
    public async Task<SoftwareStopRunResult> OnTriggeredAsync(StopLossTriggered triggered, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggered);

        var candidates = stops.FindActiveSoftwareStops(triggered.Symbol, triggered.Market, triggered.PositionSide);
        var events = new List<object>();
        var matched = 0;
        var deferred = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 到達より後に建てたエントリーは、その到達の対象ではない（遅れて届いた到達で新しい建玉を切らない）。
            if (candidate.CreatedAt > triggered.DetectedAt)
                continue;

            // 行自身の損切りラインで判定する（台帳の損切りラインは銘柄単位で最新エントリーの値に丸められる。IADR-0344 決定4）。
            if (!Reached(candidate, triggered.Price))
                continue;

            matched++;

            // 🔴 決済の前に到達を永続化する（据え置きになっても次の到達を待たずにガードが再試行する。再起動耐性）。
            var armed = candidate.TriggeredAt is null
                ? candidate with { TriggeredAt = triggered.DetectedAt, TriggeredPrice = triggered.Price, UpdatedAt = clock.UtcNow }
                : candidate;
            if (candidate.TriggeredAt is null)
            {
                stops.Save(armed);
                _logger.LogWarning(
                    "ソフトウェア逆指値が損切りライン到達: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} ライン={StopLoss} 検知価格={Price}（成行で決済します）",
                    armed.EntryDecisionId, armed.Symbol, armed.TriggerPrice, triggered.Price);
            }

            var outcome = await TryCloseAsync(armed, snapshot: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (outcome.Event is not null)
                events.Add(outcome.Event);
            if (outcome.Kind == SoftwareStopCloseKind.Deferred)
                deferred++;
        }

        return new SoftwareStopRunResult(candidates.Count, matched, deferred, events);
    }

    /// <summary>
    /// 到達済みのソフトウェア逆指値 1 件を決済する（ハンドラとガードが共有）。<paramref name="snapshot"/> を渡せばその建玉を使う
    /// （ガードは 1 巡回に 1 回だけ照会する）。null なら照会する。
    /// </summary>
    public async Task<SoftwareStopCloseOutcome> TryCloseAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot>? snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stop);

        var outcome = await TryCloseCoreAsync(stop, snapshot, cancellationToken).ConfigureAwait(false);
        return outcome.Kind == SoftwareStopCloseKind.Deferred ? NotifyIfStalled(stop, outcome) : outcome;
    }

    private async Task<SoftwareStopCloseOutcome> TryCloseCoreAsync(
        ProtectiveStopOrder stop,
        IReadOnlyList<BrokerPositionSnapshot>? snapshot,
        CancellationToken cancellationToken)
    {
        if (!stop.IsSoftwareStop || stop.State != ProtectiveStopState.Active || stop.TriggeredAt is null)
            return SoftwareStopCloseOutcome.NotApplicable;

        if (broker is not IProtectiveOrderBroker protective || positions is null)
        {
            // 成行決済・建玉照会の能力が無い構成（内蔵 paper 等）。S1 はここでは選べない（IADR-0342 決定4）ため通常は到達しない。
            _logger.LogError(
                "ソフトウェア逆指値を決済できません（ブローカー {Provider} に成行決済または建玉照会の能力がありません）。EntryDecisionId={EntryDecisionId}",
                broker.Provider, stop.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        // 1. エントリーの約定数量を確定する。未終端なら残りを取り消し、終端になるまで決済しない。
        var entry = await ResolveEntryAsync(stop, cancellationToken).ConfigureAwait(false);
        if (entry.Deferred)
            return SoftwareStopCloseOutcome.Deferred;
        if (entry.Missing)
            return OnEntryMissing(stop);

        if (entry.FilledQuantity <= 0)
        {
            // 建玉が生じていない（未約定のまま終端）。保護の役目は無い。
            Complete(stop);
            if (!entry.CancelledByUs)
                return SoftwareStopCloseOutcome.Completed;

            return SoftwareStopCloseOutcome.CompletedWith(new SoftwareStopExecuted(
                stop.EntryDecisionId, stop.Symbol, stop.Market, SoftwareStopOutcome.EntryCancelled, Quantity: 0,
                stop.TriggerPrice, stop.TriggeredPrice ?? stop.TriggerPrice, stop.Attempt,
                CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, clock.UtcNow));
        }

        // 2. すでに同じ試行の決済を送って記録まで済んでいる場合（送信後に行の更新だけが失われたクラッシュ窓）は、
        // 建玉照会も割り当ても要らない。**新しい注文は出さず**記録の結果で行を確定する（再送しない）。
        // 🔴 記録済みの決済は割り当てより先に確定する（IADR-0344 追記(1) 3。建玉を照会できない状況でも確定できる）。
        // 🔴 確定は**保存する**（次の割り当て・次の巡回・S0 の建玉残の判定がこの値を読む）。ここで保存しないと、
        // 発注記録がまだ終端になっていない（取消の結果を照会で知った）場合に割り当てが確定値を見落とす。
        var confirmed = stop;
        if (!stop.IsEntryFillConfirmed)
        {
            confirmed = stop with { RemainingProtected = entry.FilledQuantity, UpdatedAt = clock.UtcNow };
            stops.Save(confirmed);
        }

        var attempt = confirmed.Attempt + 1;
        var closeDecisionId = ProtectiveStopIds.SoftwareCloseDecisionId(confirmed.EntryDecisionId, attempt);
        var referencePrice = confirmed.TriggeredPrice ?? confirmed.TriggerPrice;
        var alreadyPlaced = store.FindByDecisionId(closeDecisionId);
        if (alreadyPlaced is not null)
        {
            return Settle(
                confirmed, attempt, closeDecisionId, alreadyPlaced.OrderId, alreadyPlaced.Status,
                new OrderIntent(
                    confirmed.Symbol, confirmed.Market, confirmed.CloseSide, confirmed.ProductType, confirmed.Mode,
                    alreadyPlaced.Quantity, referencePrice, PositionEffect.Close, StopLossPrice: null, confirmed.FxRateToBase));
        }

        // 3. 建玉を照会する（null＝不明は据え置き。「不明」を「建玉なし」と取り違えない）。
        snapshot ??= await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（建玉を照会できません）。EntryDecisionId={EntryDecisionId}",
                confirmed.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        // 4. 残保護数量の確定と、外部要因（人手決済・強制決済・S0 の逆指値の約定）による減少の**一度きりの割り当て**。
        // 割り当ては保存されるため、次の巡回で引き直さない（持ち分が巡回ごとに揺れない。IADR-0344 追記(4)）。
        var group = ProtectiveStopNetting.ReconcileShares(
            confirmed.Symbol, confirmed.Market, confirmed.EntrySide, snapshot,
            stops.FindActive(NettingScanLimit), stops, store, clock.UtcNow);
        var current = group.FirstOrDefault(s => s.EntryDecisionId == confirmed.EntryDecisionId) ?? confirmed;
        if (current.RemainingProtected is null)
        {
            // 確定できていない（エントリーの記録がまだ終端でない）。据え置く。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（エントリーの約定数量が確定していません）。EntryDecisionId={EntryDecisionId}",
                current.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        var quantity = current.RemainingProtected.Value;
        if (quantity <= 0 && current.HasUnconfirmedExternalReduction)
        {
            // 🔴 #820 の 5 巡目監査, IADR-0344 追記(5): 主張が 0 になった理由が**まだ確定していない外部要因**。
            // 建玉照会は 1 巡回だけ過少に返り得るため、1 回の観測で行を閉じない（確定すればガードが完了させる）。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（外部要因で主張が 0 になりましたが、まだ確定していません）。"
                    + "EntryDecisionId={EntryDecisionId} 未確定={Pending}",
                current.EntryDecisionId, current.PendingExternalReduction);
            return SoftwareStopCloseOutcome.Deferred;
        }

        if (quantity <= 0)
        {
            // 残保護数量が 0 ＝この記録が守る建玉はもう無い（外部要因の割り当てで削られた・手動決済済み）。
            // **行を完了させるのは残保護数量が 0 のときだけ**である（純額や他手法の主張では完了させない。BLK-1・B3）。
            _logger.LogInformation(
                "ソフトウェア逆指値を完了します（残保護数量が 0 のため決済しません）。EntryDecisionId={EntryDecisionId}",
                current.EntryDecisionId);
            Complete(current);
            return SoftwareStopCloseOutcome.Completed;
        }

        // 5. 固定 DecisionId の成行決済（予約が取れなければ送らない）。
        var closeIntent = new OrderIntent(
            current.Symbol, current.Market, current.CloseSide, current.ProductType, current.Mode, quantity, referencePrice,
            PositionEffect.Close, StopLossPrice: null, current.FxRateToBase);

        var now = clock.UtcNow;
        if (!reservations.TryReserve(closeDecisionId, now))
        {
            // 予約済みで記録が無い＝並行処理が送信中か、送信の成否が不明。重ねて送らない（IADR-0057）。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済は発注に着手済みです（予約あり・記録なし）。重ねて発注しません。EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId}",
                current.EntryDecisionId, closeDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        BrokerOrder closeOrder;
        try
        {
            closeOrder = await protective.PlaceMarketOrderAsync(closeIntent, closeDecisionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BrokerUnavailableException)
        {
            // 確実に未発注。予約を解放して据え置く（次回は同じ DecisionId で送れる）。
            reservations.Release(closeDecisionId);
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（OpenD へ接続できません）。EntryDecisionId={EntryDecisionId}",
                current.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 届いたか不明。予約を残し（同じ DecisionId では再送しない）、滞留予約はリコンサイルの領分とする。
            _logger.LogError(ex,
                "ソフトウェア逆指値の決済の送信結果が不明です（予約を残します・人手で確認してください）。EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId}",
                current.EntryDecisionId, closeDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        var placedAt = clock.UtcNow;
        store.Save(new ExecutionRecord(
            closeDecisionId, closeOrder.OrderId, current.Symbol, current.Market, current.CloseSide, current.ProductType,
            PositionEffect.Close, quantity, referencePrice, closeOrder.FilledQuantity, closeOrder.AveragePrice,
            closeOrder.Status, SlippageCalculator.Compute(referencePrice, closeOrder.AveragePrice, current.CloseSide), placedAt));
        reservations.MarkCompleted(closeDecisionId, closeOrder.OrderId, placedAt);

        return Settle(current, attempt, closeDecisionId, closeOrder.OrderId, closeOrder.Status, closeIntent);
    }

    // 決済注文の状態から行を確定する。
    // 🔴 受理＝**受理された数量だけ残保護数量を減らす**。0 になったときだけ Completed にする（IADR-0344 追記(4) 決定6・7）。
    // 部分的にしか決済できていない行を完了させると、残りが無保護のまま黙って残る（#820 の 4 巡目監査 BLK-3）。
    // 減算は「試行番号を進める保存」と同じ 1 回で行うため、再入しても同じ試行では二度減らない
    //（再入時の試行番号は 1 つ進んでおり、その DecisionId の記録はまだ無い）。
    private SoftwareStopCloseOutcome Settle(
        ProtectiveStopOrder stop, int attempt, Guid closeDecisionId, string closeOrderId, OrderStatus status, OrderIntent closeIntent)
    {
        var now = clock.UtcNow;
        var triggeredPrice = stop.TriggeredPrice ?? stop.TriggerPrice;

        if (status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
        {
            var before = stop.RemainingProtected ?? closeIntent.Quantity;
            var remaining = Math.Max(0, before - closeIntent.Quantity);
            stops.Save(stop with
            {
                RemainingProtected = remaining,
                State = remaining == 0 ? ProtectiveStopState.Completed : ProtectiveStopState.Active,
                // 完了した行に未確定の削りを残さない（IADR-0344 追記(5)）。
                PendingExternalReduction = remaining == 0 ? 0 : stop.PendingExternalReduction,
                ExternalReductionObservations = remaining == 0 ? 0 : stop.ExternalReductionObservations,
                Attempt = attempt,
                UpdatedAt = now,
            });
            _logger.LogWarning(
                "ソフトウェア逆指値で成行決済を発注: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 数量={Quantity} 残保護数量={Remaining} CloseDecisionId={CloseDecisionId} OrderId={OrderId} 状態={Status}",
                stop.EntryDecisionId, stop.Symbol, closeIntent.Quantity, remaining, closeDecisionId, closeOrderId, status);
            var placed = new SoftwareStopExecuted(
                stop.EntryDecisionId, stop.Symbol, stop.Market, SoftwareStopOutcome.ClosePlaced, closeIntent.Quantity,
                stop.TriggerPrice, triggeredPrice, attempt, closeDecisionId, closeOrderId, closeIntent, now);
            return remaining == 0
                ? SoftwareStopCloseOutcome.CompletedWith(placed)
                : new SoftwareStopCloseOutcome(SoftwareStopCloseKind.PartiallyClosed, placed);
        }

        var exhausted = attempt % MaxCloseAttemptsPerTrigger == 0;
        stops.Save(exhausted
            ? stop with { Attempt = attempt, TriggeredAt = null, TriggeredPrice = null, UpdatedAt = now }
            : stop with { Attempt = attempt, UpdatedAt = now });
        _logger.LogError(
            "ソフトウェア逆指値の成行決済が受理されませんでした（試行 {Attempt}・状態 {Status}）。{Next} EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
            attempt, status,
            exhausted ? "到達 1 回あたりの上限に達したため、次の損切りライン到達まで再試行しません。" : "次回の巡回で再試行します。",
            stop.EntryDecisionId, stop.Symbol);

        return exhausted
            ? new SoftwareStopCloseOutcome(SoftwareStopCloseKind.Rejected, new SoftwareStopExecuted(
                stop.EntryDecisionId, stop.Symbol, stop.Market, SoftwareStopOutcome.CloseRejected, closeIntent.Quantity,
                stop.TriggerPrice, triggeredPrice, attempt, closeDecisionId, closeOrderId, CloseIntent: null, now))
            : new SoftwareStopCloseOutcome(SoftwareStopCloseKind.Rejected, null);
    }

    // エントリーの約定数量。
    // 🔴 #820 の監査: 記録が無い行の数量で決済すると、同じ銘柄の**別のエントリーの建玉**を売る（実測: 孤立行 1 件＋
    // 実在の 10 株で 20 株の決済）。記録が無いあいだは 1 株も決済しない（Missing）。猶予を過ぎたら Critical で人手へ。
    private async Task<EntryFill> ResolveEntryAsync(ProtectiveStopOrder stop, CancellationToken cancellationToken)
    {
        var record = store.FindByDecisionId(stop.EntryDecisionId);
        if (record is null)
            return new EntryFill(0, Deferred: false, CancelledByUs: false, Missing: true);

        if (OrderStatusLifecycle.IsTerminal(record.Status))
            return new EntryFill(record.FilledQuantity, Deferred: false, CancelledByUs: false);

        // 未終端（受付・一部約定）: 決済後に残りが約定すると無保護の建玉が生まれるため、先に残りを取り消す。
        try
        {
            await broker.CancelOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取消に失敗しても、既に終端（約定し切った等）なら次の照会で分かる。
            _logger.LogWarning(ex,
                "ソフトウェア逆指値の発動でエントリーの取消に失敗しました（状態を照会します）。EntryDecisionId={EntryDecisionId} OrderId={OrderId}",
                stop.EntryDecisionId, record.OrderId);
        }

        BrokerOrder? current;
        try
        {
            current = await broker.GetOrderAsync(record.OrderId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            current = null;
            _logger.LogWarning(ex, "エントリーの状態を照会できません。EntryDecisionId={EntryDecisionId}", stop.EntryDecisionId);
        }

        if (current is null || !OrderStatusLifecycle.IsTerminal(current.Status))
        {
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（エントリー {OrderId} の取消がまだ終端になっていません）。EntryDecisionId={EntryDecisionId}",
                record.OrderId, stop.EntryDecisionId);
            return new EntryFill(0, Deferred: true, CancelledByUs: false);
        }

        var filled = Math.Max(record.FilledQuantity, current.FilledQuantity);
        return new EntryFill(filled, Deferred: false, CancelledByUs: current.Status != OrderStatus.Filled);
    }

    // 🔴 #820 の監査, IADR-0344 決定5-7: エントリーの発注記録が無い孤立行。**決済は出さない。**
    // 予約のリコンサイルが記録を補える窓のあいだは据え置き、猶予を過ぎたら Critical を出して行を閉じる
    //（閉じないと毎巡回この行を引き当てて決済を試み続ける。閉じる前に必ず人手へ知らせる）。
    private SoftwareStopCloseOutcome OnEntryMissing(ProtectiveStopOrder stop)
    {
        var now = clock.UtcNow;
        var age = now - stop.CreatedAt;
        if (age < _orphanGrace)
        {
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（エントリーの発注記録が見つかりません・猶予 {Grace} 内）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                _orphanGrace, stop.EntryDecisionId, stop.Symbol);
            return SoftwareStopCloseOutcome.Deferred;
        }

        Complete(stop);
        _logger.LogError(
            "ソフトウェア逆指値を人手対応として閉じます（エントリーの発注記録が猶予 {Grace} を過ぎても見つかりません・"
                + "決済は出していません）。EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
            _orphanGrace, stop.EntryDecisionId, stop.Symbol);
        return SoftwareStopCloseOutcome.CompletedWith(new SoftwareStopExecuted(
            stop.EntryDecisionId, stop.Symbol, stop.Market, SoftwareStopOutcome.EntryMissing, Quantity: 0,
            stop.TriggerPrice, stop.TriggeredPrice ?? stop.TriggerPrice, stop.Attempt,
            CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, now));
    }

    // #820 の 4 巡目監査, IADR-0344 追記(4) 決定9: **到達済みなのに決済できない**状態が猶予を過ぎたら Critical を出す。
    // 行は Active のまま再試行を続ける（据え置きは正しい fail-safe である）。**1 行につき 1 回**しか出さない
    //（StalledNotifiedAt に記録する）——毎巡回 Critical を出すと、本当に見るべき通知が埋もれる。
    private SoftwareStopCloseOutcome NotifyIfStalled(ProtectiveStopOrder stop, SoftwareStopCloseOutcome deferred)
    {
        var current = stops.Find(stop.EntryDecisionId) ?? stop;
        if (current.State != ProtectiveStopState.Active
            || current.TriggeredAt is not { } triggeredAt
            || current.StalledNotifiedAt is not null)
        {
            return deferred;
        }

        var now = clock.UtcNow;
        if (now - triggeredAt < _settlementGrace)
            return deferred;

        stops.Save(current with { StalledNotifiedAt = now, UpdatedAt = now });
        _logger.LogError(
            "ソフトウェア逆指値が到達から {Grace} を過ぎても決済できていません（建玉が無保護で残っている可能性があります・"
                + "再試行は続けます）。EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
            _settlementGrace, current.EntryDecisionId, current.Symbol);

        return new SoftwareStopCloseOutcome(SoftwareStopCloseKind.Deferred, new SoftwareStopExecuted(
            current.EntryDecisionId, current.Symbol, current.Market, SoftwareStopOutcome.CloseStalled,
            current.RemainingProtected ?? current.Quantity, current.TriggerPrice,
            current.TriggeredPrice ?? current.TriggerPrice, current.Attempt,
            CloseDecisionId: null, CloseOrderId: null, CloseIntent: null, now));
    }

    private void Complete(ProtectiveStopOrder stop) =>
        stops.Save(stop with
        {
            State = ProtectiveStopState.Completed,
            RemainingProtected = 0,
            // 完了した行に未確定の削りを残さない（復元すべき主張が無くなったため。IADR-0344 追記(5)）。
            PendingExternalReduction = 0,
            ExternalReductionObservations = 0,
            UpdatedAt = clock.UtcNow,
        });

    // 検知時の価格が行自身の損切りラインに達しているか（買い建て: 以下 / 売り建て: 以上。市場監視の判定と同じ向き）。
    private static bool Reached(ProtectiveStopOrder stop, decimal price) =>
        stop.EntrySide == TradeSide.Buy ? price <= stop.TriggerPrice : price >= stop.TriggerPrice;

    private readonly record struct EntryFill(int FilledQuantity, bool Deferred, bool CancelledByUs, bool Missing = false);
}

/// <summary>#820, IADR-0344: 1 件の決済試行の結果。</summary>
public enum SoftwareStopCloseKind
{
    /// <summary>対象外（S1 でない・Active でない・未到達）。</summary>
    NotApplicable,

    /// <summary>行を完了した（残保護数量が 0 になった・建玉なし・未約定のエントリーを取消）。</summary>
    Completed,

    /// <summary>今は決められない（接続断・建玉不明・取消待ち・送信中）。到達の記録を残して再試行する。</summary>
    Deferred,

    /// <summary>決済注文が受理されなかった。</summary>
    Rejected,

    /// <summary>
    /// #820 の 4 巡目監査, IADR-0344 追記(4) 決定7: 決済を発注したが<b>残保護数量が残っている</b>（部分的な決済）。
    /// 行は Active のままで、残りを次の巡回・次の到達で決済する。
    /// </summary>
    PartiallyClosed,
}

/// <summary>#820, IADR-0344: 決済試行の結果と、発行すべきイベント（無ければ null）。</summary>
public sealed record SoftwareStopCloseOutcome(SoftwareStopCloseKind Kind, SoftwareStopExecuted? Event)
{
    public static readonly SoftwareStopCloseOutcome NotApplicable = new(SoftwareStopCloseKind.NotApplicable, null);
    public static readonly SoftwareStopCloseOutcome Completed = new(SoftwareStopCloseKind.Completed, null);
    public static readonly SoftwareStopCloseOutcome Deferred = new(SoftwareStopCloseKind.Deferred, null);

    public static SoftwareStopCloseOutcome CompletedWith(SoftwareStopExecuted evt) => new(SoftwareStopCloseKind.Completed, evt);
}

/// <summary>#820, IADR-0344: 1 回の到達の処理結果（候補数・到達した行数・据え置き数・発行すべきイベント）。</summary>
public sealed record SoftwareStopRunResult(int Candidates, int Matched, int Deferred, IReadOnlyList<object> Events);
