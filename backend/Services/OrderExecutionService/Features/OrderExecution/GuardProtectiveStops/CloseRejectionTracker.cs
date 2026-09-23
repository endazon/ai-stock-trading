using System.Collections.Concurrent;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// 🔴 FR-10, FR-11, UC-02, UC-06, #857, IADR-0369, IADR-0210 決定4:
// 保護喪失時の成行手仕舞いが**確認できる形で拒否された**回数（保護記録ごと）と、上限に達した後に
// **いつ知らせたか**の記憶。
//
// 「確認できた拒否」は**不明ではない**——送った成行は生きていない。したがってガードは次の巡回で
// 撃ち直してよい（据え置きの HeldCloseNotificationTracker とはここが決定的に違う）。ただし
// **同じ理由で拒否され続ける成行を 30 秒ごとに送り続けない**ため、到達 1 回あたりの上限を持つ
// S1（SoftwareStopExecutor.MaxCloseAttemptsPerTrigger）と同じ 3 回で打ち切る。
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
    /// 解決した（逆指値を張り直せた・手仕舞いが通った・記録が完了した）。数えも通知の記憶も捨てる
    /// ——次に拒否されたら、また最初から 3 回試す。
    /// </summary>
    public void Forget(Guid entryDecisionId)
    {
        _rejections.TryRemove(entryDecisionId, out _);
        _lastNotifiedAt.TryRemove(entryDecisionId, out _);
    }
}
