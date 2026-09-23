using System.Collections.Concurrent;

namespace OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;

// 🔴 FR-10, #833 項目1, IADR-0389 決定8: ソフトウェア逆指値の決済レグを**照会できない**（アダプタが null に倒した）
// あいだ、それを Critical で知らせた時刻の記憶。
//
// 「送ったが結果が不明」と「確実に未約定」は絶対に混ぜない（決定3）——不明では再武装も完了もしない。
// だがそれを無音で据え置くと、受理の時点で Completed になっている保護記録が**どの巡回にも載らないまま**
// 建玉を無保護で残す。据え置きを鳴らし続けるために、巡回（既定 30 秒）ごとではなく一定間隔で 1 回出す。
//
// 🔴 **永続化しないことが設計である**（HeldCloseNotificationTracker と同じ）。再起動で記憶が消えるので、
// 再起動後の最初の巡回は必ず鳴る。間隔は定数とする（構成キーを足さない）。
public sealed class UnresolvedCloseNotificationTracker
{
    /// <summary>同じ決済レグを鳴らし直す間隔（巡回は既定 30 秒。毎巡回 Critical を重ねない）。</summary>
    public static readonly TimeSpan RenotifyInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastNotifiedAt = new();

    /// <summary>この決済レグを（再）通知すべきか。未通知、または前回の通知から間隔がたっていれば true。</summary>
    public bool IsDue(Guid closeDecisionId, DateTimeOffset now) =>
        !_lastNotifiedAt.TryGetValue(closeDecisionId, out var last) || now - last >= RenotifyInterval;

    public void MarkNotified(Guid closeDecisionId, DateTimeOffset now) => _lastNotifiedAt[closeDecisionId] = now;

    /// <summary>解決した（終端を確認できた）。次に不明を見た巡回で改めて通知させる。</summary>
    public void Forget(Guid closeDecisionId) => _lastNotifiedAt.TryRemove(closeDecisionId, out _);
}
