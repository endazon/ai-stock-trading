using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;

// FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #820, IADR-0344 決定4・決定5: ソフトウェア逆指値の発動。
// 市場監視の損切りライン到達（StopLossTriggered）を受けて、到達したソフトウェア逆指値を**成行で 1 回だけ**決済する。
// 同じ決済処理（TryCloseAsync）を常駐ガード（ProtectiveStopGuard）が到達済みの行の再試行に使う。
//
// 🔴 **二重決済を作らない**（ADR-0040 §結果「S1 は二重決済の経路を SIMULATE に戻す」）:
//   (i)   決済レグの DecisionId は (エントリー, 試行番号) から決定的に導出し、同じ DecisionId の発注記録があれば再送しない。
//   (ii)  送る前に予約表（IADR-0057 の一意制約）で DecisionId を確保し、確保できなければ送らない（ハンドラとガードの並行・再配送）。
//   (iii) 受理で行を Completed にし、以後は突き合わせの候補に入らない（市場監視は価格が戻るまで毎巡回再発火する）。
//
// fail-safe（据え置き＝イベントなし・到達の記録は残す）:
//   - 建玉照会の null（不明）・接続断・取消した未約定エントリーがまだ終端でない → 次回（ガードの巡回 or 次の到達）で再試行する。
//   - 「不明」を「建玉なし」と取り違えて行を完了させない（IADR-0118 と同じ規律）。
public sealed class SoftwareStopExecutor(
    IBrokerAdapter broker,
    IBrokerPositionSource? positions,
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IOrderReservationStore reservations,
    IClock clock,
    ILogger<SoftwareStopExecutor>? logger = null,
    TimeSpan? orphanGrace = null)
{
    /// <summary>到達 1 回あたりの決済の試行上限（拒否が続いたら打ち切って Critical を出す。IADR-0344 決定5-6）。</summary>
    public const int MaxCloseAttemptsPerTrigger = 3;

    /// <summary>
    /// #820 の監査, IADR-0344 決定5-7: エントリーの発注記録が見つからない行を「孤立」と断じるまでの猶予。
    /// 発注直後に記録だけが遅れている場合（送信と記録のあいだのクラッシュ窓・リコンサイル待ち）を殺さない長さにする。
    /// </summary>
    public static readonly TimeSpan DefaultOrphanGrace = TimeSpan.FromMinutes(15);

    // 手法混在の按分（IADR-0344 決定5-2・決定6）で参照する Active 行の上限。保有建玉数上限（既定 3）に対して十分大きい。
    private const int NettingScanLimit = 500;

    // 決済の記録を数える窓（当日有効の注文しか出さないため 1 日で足りる。IADR-0344 決定5-2）。
    private static readonly TimeSpan CloseRecordWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// #820 の監査（B2）, IADR-0344 決定5-2: 建玉照会が約定を映すまでの遅れとして見込む余裕。
    /// この分だけ「未反映」と見なす範囲を広げる。広げ過ぎても<b>持ち分を過小に見るだけ</b>で、行は据え置かれ
    /// 次の巡回で新しい建玉を見て決済する（売り過ぎ＝反対建玉より安全な倒れ方を選ぶ）。
    /// </summary>
    private static readonly TimeSpan SnapshotLagAllowance = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = logger ?? NullLogger<SoftwareStopExecutor>.Instance;

    private readonly TimeSpan _orphanGrace = orphanGrace ?? DefaultOrphanGrace;

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
        DateTimeOffset? snapshotTakenAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stop);
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
        // 建玉照会も配分も要らない。**新しい注文は出さず**記録の結果で行を確定する（再送しない）。
        var attempt = stop.Attempt + 1;
        var closeDecisionId = ProtectiveStopIds.SoftwareCloseDecisionId(stop.EntryDecisionId, attempt);
        var referencePrice = stop.TriggeredPrice ?? stop.TriggerPrice;
        var alreadyPlaced = store.FindByDecisionId(closeDecisionId);
        if (alreadyPlaced is not null)
        {
            return Settle(
                stop, attempt, closeDecisionId, alreadyPlaced.OrderId, alreadyPlaced.Status,
                new OrderIntent(
                    stop.Symbol, stop.Market, stop.CloseSide, stop.ProductType, stop.Mode, alreadyPlaced.Quantity,
                    referencePrice, PositionEffect.Close, StopLossPrice: null, stop.FxRateToBase));
        }

        // 3. 建玉残（手法の異なる Active 行の数量・発注済みで未約定の決済を差し引く）。null＝不明は据え置き。
        if (snapshot is null)
        {
            // 🔴 照会の**前**に時刻を採る。照会の後に採ると、照会中に約定した決済を「反映済み」と誤認する。
            snapshotTakenAt = clock.UtcNow;
            snapshot = await positions.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (snapshot is null)
        {
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（建玉を照会できません）。EntryDecisionId={EntryDecisionId}",
                stop.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        // 🔴 #820 の監査: 行ごとに建玉残を上限にすると、同じ銘柄の S1 行が同じ建玉を二重に主張して売り過ぎる
        //（反対建玉＝空売りになる）。同じ銘柄・方向の S1 行へ決定的に配分し、自分の持ち分だけを決済する。
        var activeStops = stops.FindActive(NettingScanLimit);
        var allocation = ProtectiveStopNetting.AllocateSoftwareStops(
            stop.Symbol, stop.Market, stop.EntrySide, snapshot, activeStops, store,
            UnreflectedCloseQuantity(stop, activeStops, snapshotTakenAt ?? clock.UtcNow));
        var allowance = allocation.TryGetValue(stop.EntryDecisionId, out var share) ? share : 0;
        var quantity = Math.Min(entry.FilledQuantity, allowance);
        if (quantity <= 0)
        {
            if (ProtectiveStopNetting.DirectionalNet(stop, snapshot) <= 0)
            {
                // 手動決済等で建玉が既に無い。決済を出すと反対建玉を作る。
                _logger.LogInformation(
                    "ソフトウェア逆指値を完了します（建玉が残っていないため決済しません）。EntryDecisionId={EntryDecisionId}",
                    stop.EntryDecisionId);
                Complete(stop);
                return SoftwareStopCloseOutcome.Completed;
            }

            // 建玉はあるが他の S1 行へ配分済み（自分の持ち分が無い）。**完了させない**——他行が決済して建玉が減れば
            // 次の巡回で自分の持ち分が生まれる。ここで完了させると保護のない建玉が残る。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（同じ銘柄の他の記録へ建玉を配分済みで持ち分がありません）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                stop.EntryDecisionId, stop.Symbol);
            return SoftwareStopCloseOutcome.Deferred;
        }

        // 4. 固定 DecisionId の成行決済（予約が取れなければ送らない）。
        var closeIntent = new OrderIntent(
            stop.Symbol, stop.Market, stop.CloseSide, stop.ProductType, stop.Mode, quantity, referencePrice,
            PositionEffect.Close, StopLossPrice: null, stop.FxRateToBase);

        var now = clock.UtcNow;
        if (!reservations.TryReserve(closeDecisionId, now))
        {
            // 予約済みで記録が無い＝並行処理が送信中か、送信の成否が不明。重ねて送らない（IADR-0057）。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済は発注に着手済みです（予約あり・記録なし）。重ねて発注しません。EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId}",
                stop.EntryDecisionId, closeDecisionId);
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
                stop.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 届いたか不明。予約を残し（同じ DecisionId では再送しない）、滞留予約はリコンサイルの領分とする。
            _logger.LogError(ex,
                "ソフトウェア逆指値の決済の送信結果が不明です（予約を残します・人手で確認してください）。EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId}",
                stop.EntryDecisionId, closeDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        var placedAt = clock.UtcNow;
        store.Save(new ExecutionRecord(
            closeDecisionId, closeOrder.OrderId, stop.Symbol, stop.Market, stop.CloseSide, stop.ProductType,
            PositionEffect.Close, quantity, referencePrice, closeOrder.FilledQuantity, closeOrder.AveragePrice,
            closeOrder.Status, SlippageCalculator.Compute(referencePrice, closeOrder.AveragePrice, stop.CloseSide), placedAt));
        reservations.MarkCompleted(closeDecisionId, closeOrder.OrderId, placedAt);

        return Settle(stop, attempt, closeDecisionId, closeOrder.OrderId, closeOrder.Status, closeIntent);
    }

    // 決済注文の状態から行を確定する。受理＝完了、拒否＝試行を進め、上限で到達の記録を外して Critical。
    private SoftwareStopCloseOutcome Settle(
        ProtectiveStopOrder stop, int attempt, Guid closeDecisionId, string closeOrderId, OrderStatus status, OrderIntent closeIntent)
    {
        var now = clock.UtcNow;
        var triggeredPrice = stop.TriggeredPrice ?? stop.TriggerPrice;

        if (status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
        {
            stops.Save(stop with { State = ProtectiveStopState.Completed, Attempt = attempt, UpdatedAt = now });
            _logger.LogWarning(
                "ソフトウェア逆指値で成行決済を発注: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 数量={Quantity} CloseDecisionId={CloseDecisionId} OrderId={OrderId} 状態={Status}",
                stop.EntryDecisionId, stop.Symbol, closeIntent.Quantity, closeDecisionId, closeOrderId, status);
            return SoftwareStopCloseOutcome.CompletedWith(new SoftwareStopExecuted(
                stop.EntryDecisionId, stop.Symbol, stop.Market, SoftwareStopOutcome.ClosePlaced, closeIntent.Quantity,
                stop.TriggerPrice, triggeredPrice, attempt, closeDecisionId, closeOrderId, closeIntent, now));
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

    // 🔴 #820 の監査（B1・B2）, IADR-0344 決定5-2: **建玉照会がまだ映していない決済**の数量（同じ銘柄・同じ決済方向）。
    //
    // 映していないものは 2 種類ある。
    //   ① 板に残っている未約定の決済 —— 約定するまで建玉は減らない。
    //   ② **建玉を照会した後に約定した決済** —— 照会した瞬間の値には入っていない。
    //      ガードは 1 巡回に 1 回しか照会しないため、先の行の決済が即時約定で返ると②になる
    //      （監査で実測: 保有 15 株に対し 10 株＋10 株＝20 株の決済＝反対建玉）。
    //      約定から建玉へ反映されるまでの遅れを見込んで SnapshotLagAllowance だけ手前から数える。
    //
    // 🔴 **S0（ブローカー側逆指値）のレグは除く。** S0 のレグも PositionEffect=Close の記録として残るが、
    // その数量は配分側が「手法の異なる Active 行の数量」として既に差し引いている。ここで数えると二重に削られ、
    // S0 と S1 が同数なら **S1 の決済が 1 株も出ない**（監査で実測）。
    private int UnreflectedCloseQuantity(
        ProtectiveStopOrder stop,
        IReadOnlyList<ProtectiveStopOrder> activeStops,
        DateTimeOffset snapshotTakenAt)
    {
        var brokerStopLegs = activeStops
            .Where(other => other.State == ProtectiveStopState.Active
                && !other.IsSoftwareStop
                && other.Symbol == stop.Symbol
                && other.Market == stop.Market
                && other.EntrySide == stop.EntrySide)
            .Select(other => other.StopDecisionId)
            .ToHashSet();

        var unreflectedSince = snapshotTakenAt - SnapshotLagAllowance;

        return store.FindClosesSince(clock.UtcNow - CloseRecordWindow, NettingScanLimit)
            .Where(r => r.Symbol == stop.Symbol
                && r.Market == stop.Market
                && r.PositionEffect == PositionEffect.Close
                && r.Side == stop.CloseSide
                && !brokerStopLegs.Contains(r.DecisionId))
            .Sum(r => UnreflectedQuantityOf(r, r.ExecutedAt >= unreflectedSince));
    }

    // 1 件の決済記録のうち「建玉照会がまだ映していない数量」。**倒れ方は過大側**（差し引き過ぎ）に寄せる
    // ——過大なら行が据え置かれて次の巡回でやり直すだけだが、過小なら反対建玉（空売り）を作る。
    private static int UnreflectedQuantityOf(ExecutionRecord record, bool touchedAfterSnapshot) =>
        record.Status switch
        {
            // 🔴 拒否はそもそも市場へ届いていない。差し引くと、拒否のたびに持ち分が消えて**再試行できなくなる**。
            OrderStatus.Rejected => 0,
            // 照会の後に置かれた・動いた決済は丸ごと未反映とみなす（部分約定の内訳を当てにしない）。
            _ when touchedAfterSnapshot => Math.Max(0, record.Quantity),
            // それ以前の終端は建玉へ反映済み。
            _ when OrderStatusLifecycle.IsTerminal(record.Status) => 0,
            // 板に残っている未約定分は、これから建玉を減らし得る。
            _ => Math.Max(0, record.Quantity - record.FilledQuantity),
        };

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

    private void Complete(ProtectiveStopOrder stop) =>
        stops.Save(stop with { State = ProtectiveStopState.Completed, UpdatedAt = clock.UtcNow });

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

    /// <summary>行を完了した（決済を受理・建玉なし・未約定のエントリーを取消）。</summary>
    Completed,

    /// <summary>今は決められない（接続断・建玉不明・取消待ち・送信中）。到達の記録を残して再試行する。</summary>
    Deferred,

    /// <summary>決済注文が受理されなかった。</summary>
    Rejected,
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
