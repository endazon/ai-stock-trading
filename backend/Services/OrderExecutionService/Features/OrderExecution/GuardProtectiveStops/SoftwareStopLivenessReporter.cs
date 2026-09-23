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
// 台帳のライン（市場監視が比べる値）は銘柄単位で最新エントリーに丸められるため（IADR-0344 決定4）、行のトリガーと異なり得る。
//
// 🔴 **観測のみ。行の状態・決済には一切関与しない。**
// 🔴 **ストアは間隔に 1 回だけ読む**（S1 が無いときも同じ）。常駐ガードの毎巡回（既定 30 秒）に照会を足さない。
public sealed class SoftwareStopLivenessReporter(
    IClock clock,
    ILogger<SoftwareStopLivenessReporter> logger,
    TimeSpan interval)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastCheckAt;

    /// <summary>間隔が来ていれば <paramref name="loadActive"/> で Active な保護記録を読み、S1 行があれば要約を出す。</summary>
    /// <returns>要約を出したら true。</returns>
    public bool ReportIfDue(Func<IReadOnlyList<ProtectiveStopOrder>> loadActive)
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
        if (softwareStops.Count == 0)
            return false;

        logger.LogInformation(
            "ソフトウェア逆指値（S1）の保護は継続中: Active {Count} 件（要約は {Interval} に 1 回。価格との比較は市場監視が行う）: {Details}",
            softwareStops.Count, interval, string.Join(" / ", softwareStops.Select(Describe)));
        return true;
    }

    private static string Describe(ProtectiveStopOrder stop)
    {
        var remaining = stop.RemainingProtected is { } r
            ? string.Create(CultureInfo.InvariantCulture, $"{r}株")
            : "未確定（エントリー約定待ち）";
        var triggered = stop.TriggeredAt is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"到達済み（{at:O} 検知価格={stop.TriggeredPrice}）")
            : "未到達";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{stop.Symbol}/{stop.Market} {stop.EntrySide} 残保護={remaining} トリガー={stop.TriggerPrice} {triggered} EntryDecisionId={stop.EntryDecisionId}");
    }
}
