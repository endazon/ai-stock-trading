namespace TradeDecisionService.Features.TradeDecision;

// FR-04, FR-02, ADR-0048 決定 2, ADR-0003, #1118, IADR-0467 決定 4: 判断へ渡す出来高（前営業日の出来高・20 日平均比）。
// 🔴 **数値はコードで計算する**（LLM に計算させない。ADR-0003・FR-16 の趣旨）。
// 🔴 **値が得られない項目は null（不明）**。0 や「少ない」として持たない（ADR-0048 決定 2）。
//   - 前営業日の足が無い・古い（最後の足が期待する前営業日でない）・出来高が 0 以下 → すべて null（Unavailable）。
//   - 前営業日の出来高はあるが、確定足が 20 本に満たない・平均の窓に 0 以下がある → 比だけ null。
// 🔴 **20 日平均は前営業日を含む直近 20 本の単純平均**（ADR-0048 決定 2「前日までの確定足 20 本の単純平均を分母とする」）。
// 🔴 **分割**: 足は前復権（出来高も分割で調整される。#1117 の実測）を 1 回の取得で揃えたものに限る。取得をまたいで足を継ぎ足さない
// （CachedDailyBarsProvider は取引日ごとに全部を取り直す）。
public sealed record DailyVolumeContext(DateOnly? PreviousDay, long? PreviousDayVolume, decimal? Average20, decimal? RatioToAverage20)
{
    /// <summary>平均の本数（前営業日を含む直近の確定足）。</summary>
    public const int AverageWindow = 20;

    /// <summary>取得できない・前営業日の足が無い（すべて不明）。</summary>
    public static readonly DailyVolumeContext Unavailable = new(null, null, null, null);

    /// <summary>前営業日の出来高が得られたか。</summary>
    public bool HasPreviousDayVolume => PreviousDay is not null && PreviousDayVolume is not null;

    public static DailyVolumeContext From(ConfirmedDailyBars? bars)
    {
        if (bars is not { Bars.Count: > 0 })
            return Unavailable;

        var last = bars.Bars[^1];
        if (last.Date != bars.ExpectedPreviousTradingDay || last.Volume <= 0)
            return Unavailable;

        if (bars.Bars.Count < AverageWindow)
            return new DailyVolumeContext(last.Date, last.Volume, null, null);

        var window = bars.Bars.Skip(bars.Bars.Count - AverageWindow).ToList();
        if (window.Any(b => b.Volume <= 0))
            return new DailyVolumeContext(last.Date, last.Volume, null, null);

        var average = window.Sum(b => (decimal)b.Volume) / AverageWindow;
        return new DailyVolumeContext(last.Date, last.Volume, average, last.Volume / average);
    }
}
