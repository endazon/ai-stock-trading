using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-10, ADR-0048 決定3, ADR-0049 決定2, #1118, #1122, IADR-0467 決定 6, IADR-0486 決定1: 日足の口の組み立ての設定（Program.cs と試験が共有する）。
// 日足は出来高（DecisionVolume:Enabled）と損切り幅の下限の ATR（StopWidthFloor:Atr14:Enabled）が同じ singleton（キー SharedKey）を共有する。
// 🔴 どちらの設定も**既定は無効**。読みは bool.TryParse（"true"/"false" の大文字小文字を問わない）で、キーなし・読めない値（"yes" 等）は無効。
public static class DailyBarsComposition
{
    /// <summary>共有する日足の口（CachedDailyBarsProvider または NoOp）の DI キー。</summary>
    public const string SharedKey = "daily-bars-shared";

    /// <summary>判断へ出来高を渡す設定（#1118, IADR-0467 決定 6）。</summary>
    public const string DecisionVolumeFlag = "DecisionVolume:Enabled";

    /// <summary>損切り幅の下限に ATR(14) を使う設定（#1122, IADR-0486 決定1）。</summary>
    public const string StopWidthFloorAtrFlag = "StopWidthFloor:Atr14:Enabled";

    /// <summary>設定が有効か（bool.TryParse で true と読めたときだけ）。</summary>
    public static bool IsOn(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var enabled) && enabled;
}
