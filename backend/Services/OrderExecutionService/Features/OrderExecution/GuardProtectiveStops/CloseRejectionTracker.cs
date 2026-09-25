using System.Collections.Concurrent;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// 🔴 FR-10, FR-11, UC-02, UC-06, #857, IADR-0369, IADR-0210 決定4:
// 保護喪失時の成行手仕舞いが**確認できる形で拒否された**回数（保護記録ごと）と、上限に達した後に
// **いつ知らせたか**の記憶。
//
// 「確認できた拒否」は**不明ではない**——送った成行は生きていない。したがってガードは次の巡回で
// 撃ち直してよい（据え置きの HeldCloseNotificationTracker とはここが決定的に違う）。ただし
// **同じ理由で拒否され続ける成行を 30 秒ごとに送り続けない**ため、3 回で打ち切る。
// 🔴 回数は S1（SoftwareStopExecutor.MaxCloseAttemptsPerTrigger）と同じ 3 だが、**性質は違う**
// （IADR-0369 の 2026-09-24 追記）: S1 の 3 は**打ち切りではなく Critical を出す連続失敗の回数**で、S1 は
// 到達の記録を残したまま行ごとの待ち時間（永続・30 秒から倍々・最大 15 分）を置いて撃ち直しを続ける
// （#833 項目2, IADR-0344 追記(15)。かつての「到達 1 回あたりで使い切ると TriggeredAt を消す」は撤去した）。
// こちらは**保護記録ごとの累計**で打ち切り、市場の事象による再武装は無い——数えが戻るのは再起動・逆指値の再発注の成功・手仕舞いの受理（CompleteAsClosed。約定は待たない・#941）だけである。
// （#938: 記録が完了したときも捨てる。完了した記録へ成行を撃ち直すことは無いので、残しても使われず辞書が太るだけである。）
//
// 🔴 **永続化しないことが設計である。**
//   - 消える向きが安全側である: 再起動すると数えが 0 に戻り、**もう一度手仕舞いを試みる**。
//     手仕舞いは出口であり、「出口を塞いだまま忘れる」より「もう一度叩く」ほうが倒れ方として正しい
//     （撃ち直しの各回は決定的な CloseDecisionId を予約してから送るため、二重発注にはならない・IADR-0057）。
//   - 上限に達したことを**無音にしない**のはプロセス内の記憶に依存しない: 記録は Active のままであり、
//     巡回はこの建玉を見続ける。通知だけがこの記憶で間引かれ、再起動後は必ず 1 回鳴る（改定 9 と同じ作法）。
//   - 永続列（protective_stop_orders への列追加）を採らない理由は IADR-0369 決定 3 に書いた。
public sealed class CloseRejectionTracker
{
    /// <summary>上限に達した行を、改めて Critical で知らせるまでの間隔（30 秒の巡回ごとには鳴らさない）。</summary>
    public static readonly TimeSpan RenotifyInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, int> _rejections = new();

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastNotifiedAt = new();

    /// <summary>この保護記録で、確認できた拒否を何回受けたか。</summary>
    public int Count(Guid entryDecisionId) => _rejections.GetValueOrDefault(entryDecisionId);

    /// <summary>確認できた拒否を 1 回数える（数えた後の回数を返す）。</summary>
    public int Record(Guid entryDecisionId) =>
        _rejections.AddOrUpdate(entryDecisionId, 1, static (_, previous) => previous + 1);

    /// <summary>上限に達した行を（再）通知すべきか。未通知、または前回の通知から間隔がたっていれば true。</summary>
    public bool IsRenotifyDue(Guid entryDecisionId, DateTimeOffset now) =>
        !_lastNotifiedAt.TryGetValue(entryDecisionId, out var last) || now - last >= RenotifyInterval;

    public void MarkNotified(Guid entryDecisionId, DateTimeOffset now) => _lastNotifiedAt[entryDecisionId] = now;

    /// <summary>
    /// 🔴 PR #916 監査 F2, IADR-0369（2026-09-24 追記）: <b>発行できなかった</b>通知を「通知済み」として覚えない
    /// （<see cref="MarkNotified"/> の取り消し）。ガードは発行の<b>前</b>に記憶するため、発行が失敗したまま
    /// 記憶が残ると、上限に達した行は次の再通知まで最大 1 時間、無保護の建玉について黙る。
    /// <para>
    /// <b>拒否の数えは消さない</b>（<see cref="Forget"/> とはここが違う）。落ちたのは通知であって手仕舞いの事実ではない
    /// ——数えまで消すと、通知基盤の不調が証券会社への成行の撃ち直し（最大 3 本）へ化ける。
    /// </para>
    /// </summary>
    public void ForgetNotification(Guid entryDecisionId) => _lastNotifiedAt.TryRemove(entryDecisionId, out _);

    /// <summary>
    /// 🔴 #938（PR #916 監査 F4）, IADR-0369（2026-09-25 追記）: 数えか通知の記憶を持っている保護記録の一覧。
    /// ガードは巡回の冒頭でこれを引き直し、<b>Active の記録が無い</b>ものを捨てる（ガードの外——乖離の取り込み——で
    /// 完了した記録の記憶を、プロセスの寿命のあいだ持ち続けない）。
    /// </summary>
    public IReadOnlyCollection<Guid> TrackedEntryDecisionIds =>
        _rejections.Keys.Union(_lastNotifiedAt.Keys).ToArray();

    /// <summary>
    /// 解決した（逆指値を張り直せた・手仕舞いが通った・記録が完了した）。数えも通知の記憶も捨てる
    /// ——次に拒否されたら、また最初から 3 回試す。
    /// #938: 記録を完了させる<b>全経路</b>で呼ばれる（ガードの <c>MarkCompleted</c> と、ガードの外で完了した記録の引き直し）。
    /// </summary>
    public void Forget(Guid entryDecisionId)
    {
        _rejections.TryRemove(entryDecisionId, out _);
        _lastNotifiedAt.TryRemove(entryDecisionId, out _);
    }
}
