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
// 🔴 **決済数量は「その行が今この巡回で動かしてよい株数」である**（IADR-0344 追記(4)・追記(7)）。建玉の純額から毎回
// 持ち分を計算し直すのをやめ、記録が自分の残保護数量を状態として持つ（確定 → 自分の決済で減算 → 外部要因の減少は
// 2 巡回連続の観測を経てから確定）。観測と確定は ProtectiveStopNetting.ReconcileShares が行う。
// **未確定の観測は帳簿を書き換えず、その巡回の上限（EffectiveProtectedQuantity）だけを縮める**——
// 帳簿を書かないので戻す操作（復元）が要らず、「戻った建玉」と「他人の建玉」を取り違えようがない。
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
//
// 🔴 #833 項目2, IADR-0344 追記(14): **決済が続けて売れない行には、行ごとの待ち時間を置く**（永続・ハンドラとガードの両方が守る）。
//   - 待ち時間が止めるのは**新しい成行を送ること**だけである。記録済みの結果での確定・残保護数量 0 での完了・建玉照会・
//     持ち分の確定は待ち時間中も行う。
//   - **到達の記録は消さない**（打ち切りを撤去した）。拒否が何回続いても、待ち時間を置いて撃ち直しを**続ける**——
//     価格が戻って到達が途絶えてもガードが撃つ（出口を塞がない。IADR-0344 決定4「一度到達したら価格が戻っても決済する」）。
//   - Critical（CloseRejected）は連続失敗 3 回目で出し、以後 4 回ごと（抑止しない・毎回は鳴らさない）。
//
// 🔴 #833 項目3, IADR-0396: **行は必ず「保存先の最新を読み直してから」書く**（ProtectiveStopStoreUpdates.Update・楽観並行）。
// このクラスは建玉照会・成行の送信を await で跨ぐため、手元の写しは古くなり得る。古い写しの全列を書き戻すと、
// 並行に進んだ完了・再武装・試行番号を巻き戻す——巻き戻った試行番号は同じ決済を「記録済み」と読ませ、
// **帳簿を二度減らして ClosePlaced を二度出す**。変更の条件（この試行はまだ誰も確定していない等）は最新の行で判定する。
// 衝突し続けたら（ProtectiveStopConcurrencyException）書かずに エラーログ（LogError）を出して据え置き、次の巡回に委ねる。
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
    /// <summary>
    /// 決済が続けて売れなかった回数がこの値に達したら Critical（<see cref="SoftwareStopOutcome.CloseRejected"/>）を出す
    /// （IADR-0344 決定5-6）。
    /// <para>
    /// 🔴 #833 項目2, IADR-0344 追記(14): <b>打ち切りではない。</b>かつてはこの回数で到達の記録を消して次の到達まで撃たなかったが、
    /// それは価格が戻ったときに出口を塞ぐため撤去した。いまは<b>待ち時間（<see cref="CloseBackoff"/>）を置いて撃ち直しを続ける</b>。
    /// 名前は他の記録（IADR-0369 など）が引いているため変えていない。
    /// </para>
    /// </summary>
    public const int MaxCloseAttemptsPerTrigger = 3;

    /// <summary>#833 項目2, IADR-0344 追記(14): Critical を出した後、次に出すまでの連続失敗の回数（毎回は鳴らさない）。</summary>
    public const int CloseRejectedRenotifyEvery = 4;

    /// <summary>#833 項目2, IADR-0344 追記(14): 連続失敗 1 回目の後の待ち時間（以後倍々）。</summary>
    public static readonly TimeSpan CloseBackoffBase = TimeSpan.FromSeconds(30);

    /// <summary>#833 項目2, IADR-0344 追記(14): 待ち時間の上限。</summary>
    public static readonly TimeSpan CloseBackoffMax = TimeSpan.FromMinutes(15);

    /// <summary>
    /// #833 項目2, IADR-0344 追記(14): 前回の到達からこれ以上空いた到達は<b>新しい窓</b>として扱い、数えと待ち時間を 0 へ戻す。
    /// 市場監視は開場中・ラインを越えている間は 60 秒ごとに到達を出す（IADR-0380）ため、これより長い空白は
    /// 閉場を挟んだか価格が一度戻ったことを意味する。60 秒間隔の到達で戻すと待ち時間が毎分消え、拒否連発が再発する。
    /// </summary>
    public static readonly TimeSpan TriggerEpisodeGap = TimeSpan.FromMinutes(5);

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
    /// #833 項目2, IADR-0344 追記(14): 連続失敗 <paramref name="failures"/> 回目の後の待ち時間
    /// ＝ min(30 秒 × 2^(n−1), 15 分)。0 以下は待ち時間なし。
    /// </summary>
    public static TimeSpan CloseBackoff(int failures)
    {
        if (failures <= 0)
            return TimeSpan.Zero;

        // 2^(n−1) の桁あふれを避ける（上限へ達した後は同じ値）。
        var doublings = Math.Min(failures - 1, 16);
        var delay = TimeSpan.FromTicks(CloseBackoffBase.Ticks << doublings);
        return delay < CloseBackoffMax ? delay : CloseBackoffMax;
    }

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
            // 🔴 #833 項目2, IADR-0344 追記(14): 到達を受けた時刻も残し、**前回から間が空いた到達は新しい窓**として
            // 数えと待ち時間を 0 へ戻す（閉場を挟んだ・価格が一度戻った後は、待ち時間の残りを持ち越さずすぐ撃つ）。
            // 再配送・遅れて届いた古い到達（検知時刻が前回より前）は時刻を巻き戻さず、窓も開かない。
            // 🔴 #833 項目3, IADR-0396: 候補の一覧は読んだ時点の写しである。記録は保存先の最新へ当てる（楽観並行）。
            // 並行に完了した行は触らない。衝突し続けたら、この行だけ据え置いて残りの候補を続ける
            //（先に決済した行のイベントを握り潰さない）。
            ProtectiveStopOrder? before = null;
            ProtectiveStopOrder? armed;
            try
            {
                armed = stops.Update(candidate.EntryDecisionId, fresh =>
                {
                    before = fresh;
                    return fresh.IsSoftwareStop && fresh.State == ProtectiveStopState.Active ? Arm(fresh, triggered) : null;
                });
            }
            catch (ProtectiveStopConcurrencyException ex)
            {
                LogConcurrencyConflict(ex, candidate);
                deferred++;
                continue;
            }

            if (armed is null || before is null)
                continue; // 並行に完了した（候補の一覧を読んだ後に）。

            if (before.TriggeredAt is null)
            {
                _logger.LogWarning(
                    "ソフトウェア逆指値が損切りライン到達: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} ライン={StopLoss} 検知価格={Price}（成行で決済します）",
                    armed.EntryDecisionId, armed.Symbol, armed.TriggerPrice, triggered.Price);
            }
            else if (before.CloseFailures > 0 && armed.CloseFailures == 0)
            {
                _logger.LogInformation(
                    "ソフトウェア逆指値: 前回の到達から {Gap} 以上空いた到達のため、決済の待ち時間をやり直します"
                        + "（連続失敗 {Failures} 回 → 0）。EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                    TriggerEpisodeGap, before.CloseFailures, armed.EntryDecisionId, armed.Symbol);
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

        SoftwareStopCloseOutcome outcome;
        try
        {
            outcome = await TryCloseCoreAsync(stop, snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtectiveStopConcurrencyException ex)
        {
            // 🔴 #833 項目3, IADR-0396: 読み直して当て直しても衝突し続けた。**書いていない**（古い写しで上書きしない）。
            // 成行を送った後の確定で起きた場合も、同じ試行の記録が残っているので次の巡回が記録の結果で確定する
            //（送信後に行の更新だけが失われた窓と同じ経路。再送はしない）。
            LogConcurrencyConflict(ex, stop);
            return SoftwareStopCloseOutcome.Deferred;
        }

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
            // #833 項目3, IADR-0396: 並行に完了していたら何もしない（完了を知らせるのは完了させた経路である）。
            if (Complete(stop.EntryDecisionId, static _ => true) is null)
                return SoftwareStopCloseOutcome.NotApplicable;
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
        // 🔴 #833 項目3, IADR-0396: ここで**保存先の最新を読み直す**（呼び出し側の写しは巡回の先頭・到達の受信時点のもの）。
        // 以降の試行番号・残保護数量・待ち時間は最新の行から決める。並行に完了・到達の取り消しが起きていたら対象外。
        var confirmed = stops.Update(stop.EntryDecisionId, fresh =>
            fresh.IsEntryFillConfirmed
                ? fresh
                : fresh with { RemainingProtected = entry.FilledQuantity, UpdatedAt = clock.UtcNow });
        if (confirmed is null || !confirmed.IsSoftwareStop || confirmed.State != ProtectiveStopState.Active
            || confirmed.TriggeredAt is null)
        {
            return SoftwareStopCloseOutcome.NotApplicable;
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
        // 🔴 #833 項目3, IADR-0396: 群に居ない＝割り当ての時点で Active ではない（並行に完了した）。古い写しで撃たない。
        var current = group.FirstOrDefault(s => s.EntryDecisionId == confirmed.EntryDecisionId);
        if (current is null)
            return SoftwareStopCloseOutcome.NotApplicable;
        if (current.RemainingProtected is null)
        {
            // 確定できていない（エントリーの記録がまだ終端でない）。据え置く。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済を据え置きます（エントリーの約定数量が確定していません）。EntryDecisionId={EntryDecisionId}",
                current.EntryDecisionId);
            return SoftwareStopCloseOutcome.Deferred;
        }

        // 🔴 #820 の 7 巡目監査, IADR-0344 追記(7): 決済数量の上限は「**観測された建玉に見合う量**」＝
        // 帳簿からまだ確定していない観測分を引いた値である（一時的な計算であり、帳簿は確定まで書き換えない）。
        var quantity = current.EffectiveProtectedQuantity;
        if (quantity <= 0 && current.HasUnconfirmedExternalReduction)
        {
            // 🔴 #820 の 5 巡目監査, IADR-0344 追記(5)・追記(7): 動かせる株数が 0 になった理由が
            // **まだ確定していない外部要因**。建玉照会は 1 巡回だけ過少に返り得るため、1 回の観測で行を閉じない。
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
            // #833 項目3, IADR-0396: 完了の条件は最新の行で判定し直す（並行に再武装されていたら閉じない）。
            if (Complete(current.EntryDecisionId,
                    static fresh => fresh.EffectiveProtectedQuantity <= 0 && !fresh.HasUnconfirmedExternalReduction) is null)
            {
                return SoftwareStopCloseOutcome.Deferred;
            }

            _logger.LogInformation(
                "ソフトウェア逆指値を完了します（残保護数量が 0 のため決済しません）。EntryDecisionId={EntryDecisionId}",
                current.EntryDecisionId);
            return SoftwareStopCloseOutcome.Completed;
        }

        // 5. 固定 DecisionId の成行決済（予約が取れなければ送らない）。
        var closeIntent = new OrderIntent(
            current.Symbol, current.Market, current.CloseSide, current.ProductType, current.Mode, quantity, referencePrice,
            PositionEffect.Close, StopLossPrice: null, current.FxRateToBase);

        var now = clock.UtcNow;

        // 🔴 #833 項目2, IADR-0344 追記(14): 続けて売れていない行は、待ち時間が過ぎるまで**新しい成行を送らない**
        // （ハンドラ・ガードのどちらから来ても同じ）。待ち時間は撃ち直しを遅らせるだけで、止めはしない。
        if (current.NextCloseAttemptAt is { } nextAttemptAt && now < nextAttemptAt)
        {
            _logger.LogInformation(
                "ソフトウェア逆指値の決済は待ち時間中です（連続失敗 {Failures} 回・次の試行は {NextAttemptAt} 以降）。"
                    + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
                current.CloseFailures, nextAttemptAt, current.EntryDecisionId, current.Symbol);
            return SoftwareStopCloseOutcome.Deferred;
        }

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

        // 🔴 #833 項目1, IADR-0389 決定1: **受理（Accepted）は約定ではない。** ここで帳簿を減らして行を閉じるのは
        // 取引台帳の押さえ（ClosePlaced → AppendApproval）を受理の時点で取るためであり、その判断は変えない
        //（約定まで押さえないと同じ建玉を二重に売れる。#848 で塞いだ穴が開く）。
        // 受理された決済が **0 約定のまま失効・取消**された場合は、約定追跡（OrderFillPoller）が終端を確認した時点で
        // SoftwareStopReArmer が未約定残ぶんを**この行へ戻す**（State を Active に復帰させる）。
        if (status is OrderStatus.Accepted or OrderStatus.PartiallyFilled or OrderStatus.Filled)
        {
            // 🔴 #820 の 7 巡目監査, IADR-0344 追記(7): 判定の基準は「この巡回で動かしてよい株数」である。
            // **それを売り切った行は役目を終える**（実際に行動した＝観測を確定させたのと同じ）。
            // 部分的にしか決済できていない行は Active のまま残し、未確定の観測もそのまま持ち越す（BLK-3）。
            // 🔴 #833 項目3, IADR-0396: **この試行を確定するのは 1 回だけ**。最新の行の試行番号が既にこの試行に達していたら、
            // 別の経路（ハンドラとガード・再配送）が確定して ClosePlaced を出している——減算も通知も重ねない。
            // 帳簿の計算も最新の行で行う（古い写しの残保護数量から引くと、並行の観測・再武装を巻き戻す）。
            var remaining = 0;
            var saved = stops.Update(stop.EntryDecisionId, fresh =>
            {
                if (fresh.Attempt >= attempt)
                    return null;

                var books = fresh.RemainingProtected ?? closeIntent.Quantity;
                var before = fresh.IsEntryFillConfirmed ? fresh.EffectiveProtectedQuantity : closeIntent.Quantity;
                remaining = Math.Max(0, before - closeIntent.Quantity);
                return fresh with
                {
                    RemainingProtected = remaining == 0 ? 0 : Math.Max(0, books - closeIntent.Quantity),
                    State = remaining == 0 ? ProtectiveStopState.Completed : ProtectiveStopState.Active,
                    // 完了した行に未確定の観測を残さない（IADR-0344 追記(5)）。
                    PendingExternalReduction = remaining == 0 ? 0 : fresh.PendingExternalReduction,
                    ExternalReductionObservations = remaining == 0 ? 0 : fresh.ExternalReductionObservations,
                    Attempt = attempt,
                    UpdatedAt = now,
                };
            });
            if (saved is null)
                return AlreadySettled(stop, attempt, closeDecisionId);

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

        // 🔴 #833 項目2, IADR-0344 追記(14): **到達の記録は消さない**（撃ち直しを止めない）。続けて売れなかった回数を数え、
        // 行ごとの待ち時間を置く。Critical は連続失敗 3 回目と、以後 4 回ごとに出す（抑止はしない・毎回は鳴らさない）。
        // 🔴 #833 項目3, IADR-0396: 数えも最新の行から進める（古い写しの数えで上書きしない）。この試行を既に誰かが
        // 数えていたら重ねない。並行に完了した行は数えない（守る建玉が無い行に「無保護で残っている」と鳴らさない）。
        var failures = 0;
        var rejected = stops.Update(stop.EntryDecisionId, fresh =>
        {
            if (fresh.Attempt >= attempt || fresh.State != ProtectiveStopState.Active)
                return null;

            failures = fresh.CloseFailures + 1;
            return fresh with
            {
                Attempt = attempt,
                CloseFailures = failures,
                NextCloseAttemptAt = now + CloseBackoff(failures),
                UpdatedAt = now,
            };
        });
        if (rejected is null)
            return AlreadySettled(stop, attempt, closeDecisionId);

        var backoff = CloseBackoff(failures);
        var notify = failures >= MaxCloseAttemptsPerTrigger
            && (failures - MaxCloseAttemptsPerTrigger) % CloseRejectedRenotifyEvery == 0;
        _logger.LogError(
            "ソフトウェア逆指値の成行決済が受理されませんでした（試行 {Attempt}・状態 {Status}・連続失敗 {Failures} 回）。"
                + "到達の記録は残し、待ち時間 {Backoff} の後（{NextAttemptAt} 以降）に撃ち直します。EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
            attempt, status, failures, backoff, now + backoff, stop.EntryDecisionId, stop.Symbol);

        return notify
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

        // #833 項目3, IADR-0396: 並行に完了していたら重ねて知らせない。
        if (Complete(stop.EntryDecisionId, static _ => true) is null)
            return SoftwareStopCloseOutcome.NotApplicable;

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
        var now = clock.UtcNow;

        // 🔴 #833 項目3, IADR-0396: 通知済みの印は最新の行へ楽観並行で書き、**書けたときだけ**知らせる
        //（ハンドラとガードが同時に猶予を越えても Critical は 1 回）。
        ProtectiveStopOrder? current;
        try
        {
            current = stops.Update(stop.EntryDecisionId, fresh =>
                fresh.State != ProtectiveStopState.Active
                    || fresh.TriggeredAt is not { } triggeredAt
                    || fresh.StalledNotifiedAt is not null
                    || now - triggeredAt < _settlementGrace
                    ? null
                    : fresh with { StalledNotifiedAt = now, UpdatedAt = now });
        }
        catch (ProtectiveStopConcurrencyException ex)
        {
            LogConcurrencyConflict(ex, stop);
            return deferred;
        }

        if (current is null)
            return deferred;

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

    // 到達を行へ記録する（変わらなければ同じインスタンスを返す）。
    //   - TriggeredAt / TriggeredPrice は最初の到達だけを残す（IADR-0344 決定4。既に記録済みなら上書きしない）。
    //   - 🔴 #833 項目2, IADR-0344 追記(14): LastTriggerSeenAt は前へしか進めない。前回から TriggerEpisodeGap 以上空いた到達
    //     （初めての到達を含む）は新しい窓として CloseFailures / NextCloseAttemptAt を 0 / null へ戻す。
    private ProtectiveStopOrder Arm(ProtectiveStopOrder candidate, StopLossTriggered triggered)
    {
        var lastSeen = candidate.LastTriggerSeenAt;
        var advances = lastSeen is null || triggered.DetectedAt > lastSeen;
        var newWindow = lastSeen is null || triggered.DetectedAt - lastSeen.Value >= TriggerEpisodeGap;
        var resets = newWindow && (candidate.CloseFailures != 0 || candidate.NextCloseAttemptAt is not null);

        if (candidate.TriggeredAt is not null && !advances && !resets)
            return candidate;

        return candidate with
        {
            TriggeredAt = candidate.TriggeredAt ?? triggered.DetectedAt,
            TriggeredPrice = candidate.TriggeredAt is null ? triggered.Price : candidate.TriggeredPrice,
            LastTriggerSeenAt = advances ? triggered.DetectedAt : lastSeen,
            CloseFailures = resets ? 0 : candidate.CloseFailures,
            NextCloseAttemptAt = resets ? null : candidate.NextCloseAttemptAt,
            UpdatedAt = clock.UtcNow,
        };
    }

    // #833 項目3, IADR-0396: 最新の行が Active で、かつ条件を満たすときだけ完了させる（楽観並行）。完了させなければ null。
    private ProtectiveStopOrder? Complete(Guid entryDecisionId, Func<ProtectiveStopOrder, bool> stillApplies) =>
        stops.Update(entryDecisionId, fresh =>
            fresh.State != ProtectiveStopState.Active || !stillApplies(fresh)
                ? null
                : fresh with
                {
                    State = ProtectiveStopState.Completed,
                    RemainingProtected = 0,
                    // 完了した行に未確定の観測を残さない（守る主張が無くなったため。IADR-0344 追記(5)・追記(7)）。
                    PendingExternalReduction = 0,
                    ExternalReductionObservations = 0,
                    UpdatedAt = clock.UtcNow,
                });

    // 🔴 #833 項目3, IADR-0396: この試行は別の経路が既に確定していた（または行が並行に完了した）。**帳簿も通知も重ねない。**
    private SoftwareStopCloseOutcome AlreadySettled(ProtectiveStopOrder stop, int attempt, Guid closeDecisionId)
    {
        _logger.LogWarning(
            "ソフトウェア逆指値の決済（試行 {Attempt}）は別の経路が既に確定しています（並行処理）。帳簿と通知を重ねません。"
                + "EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId}",
            attempt, stop.EntryDecisionId, closeDecisionId);
        return SoftwareStopCloseOutcome.NotApplicable;
    }

    private void LogConcurrencyConflict(ProtectiveStopConcurrencyException ex, ProtectiveStopOrder stop) =>
        _logger.LogError(ex,
            "🔴 ソフトウェア逆指値の保護記録の更新が並行更新と衝突し続けました（古い写しでは上書きしていません）。"
                + "この行は今回据え置き、次の巡回で最新の行から判断し直します。EntryDecisionId={EntryDecisionId} 銘柄={Symbol}",
            stop.EntryDecisionId, stop.Symbol);

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

    /// <summary>
    /// 今は決められない（接続断・建玉不明・取消待ち・送信中・続けて売れていない行の待ち時間中〔#833 項目2〕）。
    /// 到達の記録を残して再試行する。
    /// </summary>
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
