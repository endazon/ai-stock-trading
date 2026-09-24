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

    // #938（PR #916 監査 F4 の同型）: どの保護記録（EntryDecisionId）の据え置きかも覚える。キーの CloseDecisionId は
    // 保護記録と試行番号から決まるが逆は引けないため、記録が完了したときにその記録の分をまとめて捨てられるようにする。
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset LastNotifiedAt, Guid EntryDecisionId)> _held = new();

    /// <summary>この手仕舞いレグを（再）通知すべきか。未通知、または前回の通知から間隔がたっていれば true。</summary>
    public bool IsDue(Guid closeDecisionId, DateTimeOffset now) =>
        !_held.TryGetValue(closeDecisionId, out var held) || now - held.LastNotifiedAt >= RenotifyInterval;

    public void MarkNotified(Guid closeDecisionId, Guid entryDecisionId, DateTimeOffset now) =>
        _held[closeDecisionId] = (now, entryDecisionId);

    /// <summary>解決した（完了）か、発行に失敗した。次に据え置きを見た巡回で改めて通知させる。</summary>
    public void Forget(Guid closeDecisionId) => _held.TryRemove(closeDecisionId, out _);

    /// <summary>
    /// 🔴 #938, IADR-0369（2026-09-25 追記）: 保護記録が完了した。その記録のどの試行の据え置きも、もう巡回で引かれない
    /// （完了した記録は巡回の対象に入らない）ので捨てる。捨てないとプロセスの寿命のあいだ辞書が単調に増える。
    /// </summary>
    public void ForgetEntry(Guid entryDecisionId)
    {
        foreach (var (closeDecisionId, held) in _held)
        {
            if (held.EntryDecisionId == entryDecisionId)
                _held.TryRemove(closeDecisionId, out _);
        }
    }

    /// <summary>#938: 据え置きの記憶を持っている保護記録の一覧（ガードが巡回の冒頭で引き直す）。</summary>
    public IReadOnlyCollection<Guid> TrackedEntryDecisionIds =>
        _held.Values.Select(h => h.EntryDecisionId).Distinct().ToArray();
}
