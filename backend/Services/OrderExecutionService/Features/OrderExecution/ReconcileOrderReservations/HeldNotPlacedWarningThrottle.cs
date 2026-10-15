using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;

// FR-05, NFR-09, #856, IADR-0538: 据え置いた「未発注」判定（HeldNotPlaced）の Warning を、同じ予約について状態が変わらない限り畳む。
//
// 門が閉じている配備では、未発注と判定された予約は Reserved のまま毎巡回（配備は 1 時間ごと）載り直し、
// 人が SQL で片付けるまで**永久に**同じ Warning が出続ける（2026-10-05 の故障注入の AAPL が 10 日間毎時）。
//
// 🔴 **畳むのは Warning だけである。** 呼び出し側は畳んだ巡回でも DecisionId つきの Information を出し、計器も毎巡回数える
// （門を開ける判断の記録 (b) は DecisionId を 1 件ずつ画面で確かめる。その入力を消さない）。
//
// 🔴 窓の両端を突き合わせる（traceability.repo.md 規則 11）:
//   - 前の端: 初めて見た予約・状態が変わった予約は必ず Warning（記憶を消してから判定する）。
//   - 後の端: 最後に Warning を出してから再通知の間隔が過ぎたら、続いていても Warning。
//   片側だけだと、解けた後の再発が畳まれるか、二度と再通知されないかのどちらかが空く。
//
// 記憶はプロセス内に持つ（再起動で消える＝再起動後の初回は Warning。安全側）。
public sealed class HeldNotPlacedWarningThrottle(TimeSpan reminderInterval)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _warned = [];

    private sealed record Entry(BrokerProvider? Provider, DateTimeOffset FirstWarnedAt, DateTimeOffset LastWarnedAt);

    public TimeSpan ReminderInterval { get; } = reminderInterval > TimeSpan.Zero ? reminderInterval : TimeSpan.FromHours(1);

    /// <summary>
    /// 1 巡回の判定から、据え置き 1 件ごとに Warning を出すか（<see cref="HeldNotPlacedNotice.Warn"/>）を決め、記憶を進める。
    /// </summary>
    /// <param name="verdicts">その巡回の確定しなかった判定（全種類）。</param>
    /// <param name="scanComplete">その巡回が滞留の全件を走査したか（バッチが満杯でない）。全件なら、載らなかった予約の記憶を消す。</param>
    /// <param name="now">巡回の時刻。</param>
    public IReadOnlyList<HeldNotPlacedNotice> Classify(
        IReadOnlyList<ReservationReconciliationVerdict> verdicts, bool scanComplete, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        var notices = new List<HeldNotPlacedNotice>();
        lock (_gate)
        {
            // 状態が変わった予約（据え置き以外の判定になった）は記憶を消す。次に据え置かれたら初回として Warning にする。
            foreach (var other in verdicts.Where(v => v.Kind != ReservationReconciliationVerdictKind.HeldNotPlaced))
                _warned.Remove(other.DecisionId);

            var held = verdicts.Where(v => v.Kind == ReservationReconciliationVerdictKind.HeldNotPlaced).ToList();

            // 全件を走査した巡回に載らなかった予約は、もう滞留していない（確定・人の SQL・解放）。記憶を消す。
            // バッチが満杯の巡回では、走査されなかっただけかもしれないので消さない（次に載れば、続いている据え置きとして扱う）。
            if (scanComplete)
            {
                var heldIds = held.Select(h => h.DecisionId).ToHashSet();
                foreach (var gone in _warned.Keys.Where(id => !heldIds.Contains(id)).ToList())
                    _warned.Remove(gone);
            }

            foreach (var h in held)
            {
                if (_warned.TryGetValue(h.DecisionId, out var entry)
                    && entry.Provider == h.ReservationProvider
                    && now - entry.LastWarnedAt < ReminderInterval)
                {
                    notices.Add(new HeldNotPlacedNotice(
                        h.DecisionId, h.ReservationProvider, Warn: false, entry.FirstWarnedAt, entry.LastWarnedAt + ReminderInterval));
                    continue;
                }

                // 初回・取引環境が変わった・再通知の間隔が過ぎた。取引環境が変わったら初回からやり直す。
                var first = entry is not null && entry.Provider == h.ReservationProvider ? entry.FirstWarnedAt : now;
                _warned[h.DecisionId] = new Entry(h.ReservationProvider, first, now);
                notices.Add(new HeldNotPlacedNotice(h.DecisionId, h.ReservationProvider, Warn: true, first, now + ReminderInterval));
            }
        }

        return notices;
    }
}

/// <summary>据え置き 1 件の通知の仕方。<see cref="Warn"/> が false なら Information で継続だけを残す。</summary>
public sealed record HeldNotPlacedNotice(
    Guid DecisionId,
    BrokerProvider? ReservationProvider,
    bool Warn,
    DateTimeOffset FirstWarnedAt,
    DateTimeOffset NextWarnAt);
