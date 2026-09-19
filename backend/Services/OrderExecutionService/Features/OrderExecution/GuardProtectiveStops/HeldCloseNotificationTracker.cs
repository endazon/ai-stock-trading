using System.Collections.Concurrent;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// 🔴 FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 9）, IADR-0210:
// 据え置き中の成行手仕舞い（予約あり・記録なし＝**届いたか不明**）を、**このプロセスがいつ通知したか**の記憶。
//
// 保護逆指値ガードは「予約だけがある」あいだ成行も逆指値も重ねずに据え置く（改定 7）。その据え置きを無音にしないために、
//   1. **このプロセスがまだ通知していない**（＝再起動後の最初の巡回／送信中にプロセスが止まった後／発行前に止まった後）、
//   2. **前回の通知から RenotifyInterval たった**（通知を 1 回見逃すと逆指値なしの建玉が無期限に残る）、
// のどちらかで CloseDispatchIndeterminate（Critical・CloseIntent つき）を発行し直す。
//
// 🔴 **永続化しないことが設計である。** 再起動で記憶が消えるので、再起動後の最初の巡回は必ず発行する。
// 送信中にプロセスが止まると予約だけが残りイベントは 1 通も出ていない（＝台帳が一切押さえていない）が、
// どの予約が「通知済み」かを永続化していたら、発行前に止まった場合の穴は塞がらない。
// 再発行は安全である——取引台帳の承認の追加は DecisionId で冪等（窓も延びない）、通知と監査は重複しても害が無い。
// 間隔は定数とする（構成キーを足さない。30 秒の巡回ごとに Critical を重ねない判断は IADR-0210 の追記のまま）。
public sealed class HeldCloseNotificationTracker
{
    public static readonly TimeSpan RenotifyInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastNotifiedAt = new();

    /// <summary>この手仕舞いレグを（再）通知すべきか。未通知、または前回の通知から間隔がたっていれば true。</summary>
    public bool IsDue(Guid closeDecisionId, DateTimeOffset now) =>
        !_lastNotifiedAt.TryGetValue(closeDecisionId, out var last) || now - last >= RenotifyInterval;

    public void MarkNotified(Guid closeDecisionId, DateTimeOffset now) => _lastNotifiedAt[closeDecisionId] = now;

    /// <summary>解決した（完了）か、発行に失敗した。次に据え置きを見た巡回で改めて通知させる。</summary>
    public void Forget(Guid closeDecisionId) => _lastNotifiedAt.TryRemove(closeDecisionId, out _);
}
