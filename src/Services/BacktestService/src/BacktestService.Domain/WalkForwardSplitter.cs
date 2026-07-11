namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008: ウォークフォワードの窓（In-Sample / Out-of-Sample、いずれも両端含む）。
public readonly record struct WalkForwardWindow(
    DateOnly InSampleStart,
    DateOnly InSampleEnd,
    DateOnly OutOfSampleStart,
    DateOnly OutOfSampleEnd);

// FR-15, 06_daytrading-review §3.2, IADR-0044: ウォークフォワード分割。IS で最適化し OOS で評価することで
// In-Sample 最適化の楽観（過剰適合）を排す。ローリング（固定幅スライド）とアンカー（起点固定・IS 拡大）を提供する。
public static class WalkForwardSplitter
{
    // ローリング: IS 幅・OOS 幅を固定して OOS 幅ぶんスライドする。OOS が期間内に収まる窓のみ返す。
    public static IReadOnlyList<WalkForwardWindow> Rolling(DateOnly start, DateOnly end, int inSampleDays, int outOfSampleDays)
    {
        Validate(inSampleDays, outOfSampleDays);
        var windows = new List<WalkForwardWindow>();
        for (var k = 0; ; k++)
        {
            var isStart = start.AddDays(k * outOfSampleDays);
            var isEnd = isStart.AddDays(inSampleDays - 1);
            var oosStart = isStart.AddDays(inSampleDays);
            var oosEnd = oosStart.AddDays(outOfSampleDays - 1);
            if (oosEnd > end)
                break;
            windows.Add(new WalkForwardWindow(isStart, isEnd, oosStart, oosEnd));
        }
        return windows;
    }

    // アンカー: IS 起点を固定し、各ステップで IS を OOS 幅ぶん拡大する。OOS が期間内に収まる窓のみ返す。
    public static IReadOnlyList<WalkForwardWindow> Anchored(DateOnly start, DateOnly end, int initialInSampleDays, int outOfSampleDays)
    {
        Validate(initialInSampleDays, outOfSampleDays);
        var windows = new List<WalkForwardWindow>();
        for (var k = 0; ; k++)
        {
            var isEnd = start.AddDays(initialInSampleDays - 1 + k * outOfSampleDays);
            var oosStart = isEnd.AddDays(1);
            var oosEnd = oosStart.AddDays(outOfSampleDays - 1);
            if (oosEnd > end)
                break;
            windows.Add(new WalkForwardWindow(start, isEnd, oosStart, oosEnd));
        }
        return windows;
    }

    private static void Validate(int inSampleDays, int outOfSampleDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inSampleDays, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outOfSampleDays, 1);
    }
}
