using System.Globalization;
using Microsoft.Extensions.Logging;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Domain;

namespace OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

// FR-10, UC-02, ADR-0040 決定1（S1）, #902, IADR-0365 決定5: Active なソフトウェア逆指値（S1）の行を低頻度で要約する。
//
// S1 の行は配置時に 1 回ログが出るだけで、常駐ガードは未到達の行を StillActive として数えるだけで何も出さない。
// 「S1 の保護がまだ生きているか・ガードが回っているか・行のトリガーはいくらか」を運用者が判定できない（#902）。
// 価格とラインの比較は市場監視が行う（そちらの要約は市場監視の StopLossLivenessReporter）。ここは発注執行が持つ行の側を示す。
// 台帳のライン（市場監視が比べる値）は銘柄単位に 1 本で、保有中のエントリーのうち最も保護的な値である（#936, IADR-0393。
// 以前は最新エントリーに丸められていた＝IADR-0344 決定4）。同じ銘柄に行が複数あれば、行のトリガーと異なり得る。
//
// 🔴 **観測のみ。行の状態・決済には一切関与しない。**
// 🔴 **ストアは間隔に 1 回だけ読む**（S1 が無いときも同じ）。常駐ガードの毎巡回（既定 30 秒）に照会を足さない。
// 🔴 #856, IADR-0538: **発注の記録が無いまま未確定で残る S1（幽霊の疑い）を識別する。** 送る前に結果が不明になった新規建て
// （2026-10-05 の故障注入 BeforeSend）では、S1 の記録は発注前に武装済みで、エントリーの発注記録は永久にできない。ガードは
// 「これから約定し得る」とみなして据え置き、閉じる経路は損切りラインへの到達だけである。要約に印を付け、後始末の手順へ
// 案内する Warning を初回と再通知の間隔ごとに出す。**行を閉じる・消すことはしない**（利用者の判断。Runbook の SQL）。
public sealed class SoftwareStopLivenessReporter(
    IClock clock,
    ILogger<SoftwareStopLivenessReporter> logger,
    TimeSpan interval,
    TimeSpan? unrecordedEntryAge = null,
    TimeSpan? unrecordedReminderInterval = null)
{
    /// <summary>#856, IADR-0538: 発注記録の無い未確定の S1 を「幽霊の疑い」とみなすまでの経過の既定（24 時間）。</summary>
    public static readonly TimeSpan DefaultUnrecordedEntryAge = TimeSpan.FromHours(24);

    /// <summary>#856, IADR-0538: 幽霊の疑いの Warning を同じ行について再び出すまでの間隔の既定（24 時間）。</summary>
    public static readonly TimeSpan DefaultUnrecordedReminderInterval = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private readonly TimeSpan _unrecordedEntryAge = unrecordedEntryAge is { } age && age > TimeSpan.Zero ? age : DefaultUnrecordedEntryAge;
    private readonly TimeSpan _unrecordedReminder =
        unrecordedReminderInterval is { } r && r > TimeSpan.Zero ? r : DefaultUnrecordedReminderInterval;
    private readonly Dictionary<Guid, DateTimeOffset> _unrecordedWarnedAt = [];
    private DateTimeOffset? _lastCheckAt;

    /// <summary>間隔が来ていれば <paramref name="loadActive"/> で Active な保護記録を読み、S1 行があれば要約を出す。</summary>
    /// <param name="loadActive">Active な保護記録を読む。</param>
    /// <param name="entryRecordExists">
    /// #856, IADR-0538: エントリーの発注記録（executed_orders）があるか。null なら幽霊の疑いを判定しない。
    /// 呼ぶのは未確定・未到達・経過済みの候補にだけである（通常 0 件）。
    /// </param>
    /// <returns>要約を出したら true。</returns>
    public bool ReportIfDue(
        Func<IReadOnlyList<ProtectiveStopOrder>> loadActive, Func<Guid, bool>? entryRecordExists = null)
    {
        ArgumentNullException.ThrowIfNull(loadActive);

        var now = clock.UtcNow;
        lock (_gate)
        {
            if (_lastCheckAt is { } last && now - last < interval)
                return false;
            _lastCheckAt = now;
        }

        var softwareStops = loadActive()
            .Where(s => s.IsSoftwareStop && s.State == ProtectiveStopState.Active)
            .OrderBy(s => s.CreatedAt)
            .ToList();

        var unrecorded = entryRecordExists is null
            ? []
            : softwareStops
                .Where(s => s.RemainingProtected is null && s.TriggeredAt is null && now - s.CreatedAt >= _unrecordedEntryAge)
                .Where(s => !entryRecordExists(s.EntryDecisionId))
                .ToList();
        var unrecordedIds = unrecorded.Select(s => s.EntryDecisionId).ToHashSet();

        if (softwareStops.Count > 0)
        {
            logger.LogInformation(
                "ソフトウェア逆指値（S1）の保護は継続中: Active {Count} 件（要約は {Interval} に 1 回。価格との比較は市場監視が行う）: {Details}",
                softwareStops.Count, interval,
                string.Join(" / ", softwareStops.Select(s => Describe(s, unrecordedIds.Contains(s.EntryDecisionId)))));
        }

        ReportUnrecorded(unrecorded, unrecordedIds, entryRecordExists is not null, now);
        return softwareStops.Count > 0;
    }

    // #856, IADR-0538: 幽霊の疑いの Warning。同じ行は初回と再通知の間隔ごとだけ（窓の両端: 該当しなくなったら記憶を消し、
    // 続いていても間隔が過ぎたら出す）。判定しなかった呼び出し（照会の口が無い）では記憶を動かさない。
    private void ReportUnrecorded(
        IReadOnlyList<ProtectiveStopOrder> unrecorded, HashSet<Guid> unrecordedIds, bool judged, DateTimeOffset now)
    {
        if (!judged)
            return;

        List<ProtectiveStopOrder> due;
        lock (_gate)
        {
            foreach (var gone in _unrecordedWarnedAt.Keys.Where(id => !unrecordedIds.Contains(id)).ToList())
                _unrecordedWarnedAt.Remove(gone);

            due = unrecorded
                .Where(s => !_unrecordedWarnedAt.TryGetValue(s.EntryDecisionId, out var at) || now - at >= _unrecordedReminder)
                .ToList();
            foreach (var s in due)
                _unrecordedWarnedAt[s.EntryDecisionId] = now;
        }

        foreach (var stop in due)
        {
            logger.LogWarning(
                "ソフトウェア逆指値（S1）の記録が、エントリーの発注記録の無いまま {Age} 以上「残保護=未確定」で残っています（幽霊の疑い。"
                    + "銘柄={Symbol}/{Market} 方向={Side} 数量={Quantity} トリガー={TriggerPrice} EntryDecisionId={EntryDecisionId} 作成={CreatedAt:O}）。"
                    + "エントリーは送られていないか、送信結果が不明で予約が滞留しています。ガードはこの行を閉じず、損切りラインに届くまで残ります"
                    + "（同じ銘柄・方向の別の手法の新規建ては見送られます）。システムは自動で閉じません。"
                    + "発注経路の Runbook「発注の記録が無いまま残った S1 の記録を閉じる」に従い、予約の状態と証券会社の画面を確かめてから利用者が判断してください。"
                    + "この警告は同じ行について {Reminder} ごとにだけ出します。",
                _unrecordedEntryAge, stop.Symbol, stop.Market, stop.EntrySide, stop.Quantity, stop.TriggerPrice,
                stop.EntryDecisionId, stop.CreatedAt, _unrecordedReminder);
        }
    }

    private static string Describe(ProtectiveStopOrder stop, bool unrecorded)
    {
        var remaining = stop.RemainingProtected is { } r
            ? string.Create(CultureInfo.InvariantCulture, $"{r}株")
            : "未確定（エントリー約定待ち）";
        var triggered = stop.TriggeredAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"到達済み（{at:O} 検知価格={stop.TriggeredPrice}）")
            : "未到達";
        var ghost = unrecorded ? " 発注記録なし（幽霊の疑い）" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{stop.Symbol}/{stop.Market} {stop.EntrySide} 残保護={remaining} トリガー={stop.TriggerPrice} {triggered}{ghost} EntryDecisionId={stop.EntryDecisionId}");
    }
}
