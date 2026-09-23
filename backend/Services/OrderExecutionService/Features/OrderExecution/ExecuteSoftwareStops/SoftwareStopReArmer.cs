using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;

// 🔴 FR-10, FR-12, UC-02, ADR-0040 決定1（S1）, #833 項目1, IADR-0389: **受理だけで完了させた保護記録の再武装**。
//
// SoftwareStopExecutor.Settle は決済注文の Accepted を約定と同じように扱い、受理数量ぶん残保護数量を減らして
// 0 になった行を Completed にする（IADR-0344 決定5-5）。moomoo の模擬取引の注文は**当日限り**であり、
// 受理された決済が **0 約定のまま失効・取消**され得る。そのとき建玉は 1 株も減っていないのに行は Completed で、
// FindActive にも FindActiveSoftwareStops にも載らない＝**ガードも到達ハンドラも二度と見ない**。
// 通知も 1 本も出ない（無音で保護が消える）。
//
// 🔴 **根拠は「自分が出した注文 1 件の終端状態」だけである。**
// IADR-0344 追記(7) が撤去した ProtectiveStopNetting.Restore は**建玉照会の純額**から「自分の建玉が戻った」を
// 推測しており、他人の建玉（S2・人手）と区別できずに 3 巡にわたって不可逆な売り過ぎを作った。
// ここは純額を一切見ない——約定しなかった株数は、その注文の Quantity と FilledQuantity の差である。
//
// 🔴 **「送ったが結果が不明」を「確実に未約定」と混ぜない**（IADR-0389 決定3・IADR-0118 と同じ規律）:
//   - 再武装してよいのは OrderStatusLifecycle.AbandonsUnfilledRemainder（Cancelled / Rejected / Expired）だけ。
//     **Filled は含めない**（IsTerminal ではこれを拾ってしまう）。
//   - 照会が null（不明）では何も書かず、据え置きを Critical で鳴らす（OnCloseUnresolved）。
//
// **注文は 1 株も出さない。** 実際に売るのは従来どおり SoftwareStopExecutor.TryCloseAsync であり、そこは
// 送る前に必ず建玉を照会し（null は据え置き）ProtectiveStopNetting.ReconcileShares を通すため、
// 戻した主張が実建玉より多ければ**売る前に**外部要因の観測として削られる。
public sealed class SoftwareStopReArmer(
    IProtectiveStopOrderStore stops,
    IExecutedOrderStore store,
    IClock clock,
    ILogger<SoftwareStopReArmer>? logger = null,
    UnresolvedCloseNotificationTracker? unresolved = null)
{
    /// <summary>決済レグの持ち主を探すときに走査する完了済み S1 行の上限（実運用の同一銘柄の行数は 1 桁）。</summary>
    public const int CompletedScanLimit = 50;

    /// <summary>1 行あたり再導出する試行番号の数（現在値から下へ。実運用の試行数は 1 桁）。</summary>
    public const int AttemptScanLimit = 50;

    private readonly ILogger _logger = logger ?? NullLogger<SoftwareStopReArmer>.Instance;

    /// <summary>
    /// FR-10, #833 項目1, IADR-0389 決定2・3・5: 決済レグが<b>確認できた終端</b>になったときに呼ぶ。
    /// 未約定残があれば保護記録を再武装し、発行すべき <see cref="SoftwareStopExecuted"/> を返す（発行は呼び出し側）。
    /// <para>
    /// S1 の決済レグでない（エントリー・S0 の手仕舞い・他サービスの注文）なら <c>null</c> を返す。
    /// 🔴 <b>一致しないことは異常ではない</b>——約定追跡は全注文を見る。
    /// </para>
    /// </summary>
    public SoftwareStopExecuted? OnCloseTerminalized(ExecutionRecord close, OrderStatus status, int filledQuantity)
    {
        ArgumentNullException.ThrowIfNull(close);

        // 決済レグ以外は候補にしない（エントリーの終端化で毎回走査しない）。
        if (close.PositionEffect != PositionEffect.Close)
            return null;

        // 🔴 Filled を含む IsTerminal では**全量約定でも戻してしまう**。問いは「未約定残が二度と約定しないか」である。
        if (!OrderStatusLifecycle.AbandonsUnfilledRemainder(status))
            return null;

        var stop = ResolveSoftwareStop(close);
        if (stop is null)
            return null;

        // 終端を確認できた＝据え置きの記憶を落とす（次に不明を見た巡回で改めて鳴らす）。
        unresolved?.Forget(close.DecisionId);

        var unfilled = close.Quantity - Math.Max(0, filledQuantity);
        if (unfilled <= 0)
        {
            // 受理した数量を全部売れていた（状態は Cancelled 等だが残は無い）。帳簿は正しいので触らない。
            return null;
        }

        // 🔴 上限＝エントリーの約定数量（＝この行が守り得る最大の株数）。記録が無ければ行の承認数量。
        // 同じレグを二度観測しても行の主張がエントリーの建玉を超えない（IADR-0389 決定5）。
        var entry = store.FindByDecisionId(stop.EntryDecisionId);
        var current = stop.RemainingProtected ?? 0;
        var cap = Math.Max(entry?.FilledQuantity ?? stop.Quantity, current);
        var restored = Math.Min(current + unfilled, cap);
        if (restored <= 0)
        {
            // エントリーが 1 株も約定していない（＝守る建玉が無い）。完了のままが正しい。
            _logger.LogWarning(
                "ソフトウェア逆指値の決済が未約定で終了しましたが、エントリーの約定数量が 0 のため再武装しません。"
                    + "EntryDecisionId={EntryDecisionId} CloseDecisionId={CloseDecisionId}",
                stop.EntryDecisionId, close.DecisionId);
            return null;
        }

        var now = clock.UtcNow;
        stops.Save(stop with
        {
            RemainingProtected = restored,
            State = ProtectiveStopState.Active,
            // 到達の記録（TriggeredAt / TriggeredPrice）は消さない——一度到達したら価格が戻っても決済する
            // （IADR-0344 決定4）。次のガード巡回が新しい試行 ID で撃ち直す。
            // 据え置きの通知済みフラグは落とす（再武装した後も決済できなければ、改めて鳴らすべきである）。
            StalledNotifiedAt = null,
            UpdatedAt = now,
        });

        _logger.LogError(
            "🔴 ソフトウェア逆指値の成行決済が約定しないまま終了しました（状態 {Status}・発注 {Ordered} 株・約定 {Filled} 株）。"
                + "未約定の {Unfilled} 株は建玉に残っています。保護記録を再武装しました（残保護数量 {Restored}・Active）。"
                + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol} CloseDecisionId={CloseDecisionId} OrderId={OrderId}",
            status, close.Quantity, filledQuantity, unfilled, restored,
            stop.EntryDecisionId, stop.Symbol, close.DecisionId, close.OrderId);

        return new SoftwareStopExecuted(
            stop.EntryDecisionId, stop.Symbol, stop.Market, SoftwareStopOutcome.CloseUnfilled, unfilled,
            stop.TriggerPrice, stop.TriggeredPrice ?? stop.TriggerPrice, stop.Attempt,
            close.DecisionId, close.OrderId, CloseIntent: null, now);
    }

    /// <summary>
    /// FR-10, #833 項目1, IADR-0389 決定8: 決済レグを<b>照会できなかった</b>（不明）ときに呼ぶ。
    /// <para>
    /// 🔴 <b>再武装も完了もしない。</b>記録は非終端のまま残り、次の巡回で引き直される。
    /// ただし受理の時点で保護記録は完了しているため、黙って据え置くと建玉が無保護のまま誰の巡回にも載らない。
    /// 一定間隔で 1 回 Critical を出す（プロセス内の記憶。再起動後の最初の巡回は必ず鳴る）。
    /// </para>
    /// </summary>
    public void OnCloseUnresolved(ExecutionRecord close)
    {
        ArgumentNullException.ThrowIfNull(close);

        if (close.PositionEffect != PositionEffect.Close)
            return;

        var stop = ResolveSoftwareStop(close);
        if (stop is null)
            return;

        var now = clock.UtcNow;
        if (unresolved is not null && !unresolved.IsDue(close.DecisionId, now))
            return;

        unresolved?.MarkNotified(close.DecisionId, now);
        _logger.LogError(
            "🔴 ソフトウェア逆指値の成行決済の結果を照会できません（{Ordered} 株・状態不明のまま据え置き）。"
                + "**約定したか取り消されたかが分からないため再武装しません**（不明を未約定と取り違えない）。"
                + "保護記録は {State} のままです。建玉を手で確認してください。"
                + "EntryDecisionId={EntryDecisionId} 銘柄={Symbol} CloseDecisionId={CloseDecisionId} OrderId={OrderId}",
            close.Quantity, stop.State, stop.EntryDecisionId, stop.Symbol, close.DecisionId, close.OrderId);
    }

    // 決済レグの DecisionId から持ち主の S1 行を引く。
    // 🔴 **新しい列を足さない**（IADR-0389 決定4）。SoftwareCloseDecisionId(entry, attempt) は決定的なので、
    // 銘柄・市場・反対方向で候補行を絞り、試行番号を行の現在値から下へ再導出して突き合わせる。
    // **完了済みの行も引く**——受理で完了させられた行こそが本件の対象である。
    private ProtectiveStopOrder? ResolveSoftwareStop(ExecutionRecord close)
    {
        var entrySide = close.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
        var candidates = stops.FindActiveSoftwareStops(close.Symbol, close.Market, entrySide)
            .Concat(stops.FindCompletedSoftwareStops(close.Symbol, close.Market, entrySide, CompletedScanLimit));

        foreach (var candidate in candidates)
        {
            var lowest = Math.Max(1, candidate.Attempt - AttemptScanLimit + 1);
            for (var attempt = candidate.Attempt; attempt >= lowest; attempt--)
            {
                if (ProtectiveStopIds.SoftwareCloseDecisionId(candidate.EntryDecisionId, attempt) == close.DecisionId)
                    return candidate;
            }
        }

        return null;
    }
}
