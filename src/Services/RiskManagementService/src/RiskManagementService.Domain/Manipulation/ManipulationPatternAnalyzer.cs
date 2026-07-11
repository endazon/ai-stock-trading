using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain.Manipulation;

// FR-19, ADR-0007, IADR-0037: 相場操縦とみなされ得る発注パターンの検知（純関数コア）。
// 自口座の直近窓（既定 5 分）の発注統計に対するヒューリスティックで、見せ玉・過剰訂正取消・自己レイヤリングを近似検知する。
// 決定的（IADR-0003/0004）で、RiskEvaluator から同期的に呼べる。市場全体の板（他者注文）は本コアの対象外（IADR-0037）。
public static class ManipulationPatternAnalyzer
{
    public static ManipulationVerdict Analyze(OrderActivityWindow window, ManipulationDetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        var records = window.Records;
        var placements = records.Count;

        // 最小標本ガード: 発注数がしきい値未満の窓は常に無嫌疑（低頻度の正常取引での誤検知を防ぐ安全側の既定）。
        if (placements < settings.MinimumSampleSize)
        {
            return ManipulationVerdict.Clean;
        }

        var signals = new List<ManipulationSignal>();

        var cancelledWithoutFill = records.Where(r => r.IsCancelledWithoutFill).ToList();

        // 1. 過剰な取消: 約定なし取消が発注に占める比率が過大。
        var cancellationRatio = (decimal)cancelledWithoutFill.Count / placements;
        if (cancellationRatio > settings.MaxCancellationRatio)
        {
            signals.Add(ManipulationSignal.ExcessiveCancellations);
        }

        // 2. 過剰な訂正: 1 発注あたりの訂正反復が過大。
        var amendmentsPerOrder = (decimal)records.Sum(r => r.AmendmentCount) / placements;
        if (amendmentsPerOrder > settings.MaxAmendmentsPerOrder)
        {
            signals.Add(ManipulationSignal.ExcessiveAmendments);
        }

        // 3. 見せ玉（約定意思のない発注）: 低約定率かつ短命取消の反復。
        var fillRatio = (decimal)records.Count(r => r.IsFilledOrPartial) / placements;
        var shortLivedCancels = cancelledWithoutFill.Count(r =>
            r.Lifetime is { } lifetime && lifetime <= settings.ShortLivedCancelThreshold);
        if (fillRatio < settings.MinFillRatio && shortLivedCancels >= settings.MaxShortLivedCancels)
        {
            signals.Add(ManipulationSignal.NoExecutionIntent);
        }

        // 4. 板演出（レイヤリング）: 同一方向の約定なし取消が同時に多段生存。
        if (HasLayering(cancelledWithoutFill, window.AsOf, settings.LayeringOrderCount))
        {
            signals.Add(ManipulationSignal.Layering);
        }

        return signals.Count > 0 ? ManipulationVerdict.Flag(signals) : ManipulationVerdict.Clean;
    }

    // 同一売買方向ごとに、約定なし取消注文の生存区間 [PlacedAt, TerminalAt ?? AsOf] の最大同時本数を求め、
    // しきい値以上なら板演出とみなす。終端未確定の注文は基準時刻まで生存中として扱う。
    private static bool HasLayering(
        IReadOnlyList<OrderActivityRecord> cancelledWithoutFill, DateTimeOffset asOf, int threshold)
    {
        foreach (var side in new[] { TradeSide.Buy, TradeSide.Sell })
        {
            var intervals = cancelledWithoutFill
                .Where(r => r.Side == side)
                .Select(r => (Start: r.PlacedAt, End: r.TerminalAt ?? asOf))
                .ToList();

            if (MaxConcurrent(intervals) >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    // 区間の最大同時重なり本数（スイープライン）。端点が一致する区間は重なりとして数える（同時に板へ並ぶ扱い）。
    private static int MaxConcurrent(List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0)
        {
            return 0;
        }

        var starts = intervals.Select(i => i.Start).OrderBy(t => t).ToList();
        var ends = intervals.Select(i => i.End).OrderBy(t => t).ToList();

        int i = 0, j = 0, current = 0, max = 0;
        while (i < starts.Count)
        {
            if (starts[i] <= ends[j])
            {
                current++;
                max = Math.Max(max, current);
                i++;
            }
            else
            {
                current--;
                j++;
            }
        }

        return max;
    }
}
