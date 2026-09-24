using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, #935, IADR-0394: 1 銘柄の決済の承認（LedgerCloseApproval）から、**当日の損切りの有無**を建玉の方向ごとに
// 3 値（無し／損切り済み／不明）で射影する純関数。DB・時計に依存しない（now は呼び出し側が与える）。
//
// 数える規則（決定1。裁定が仕様書での明記を求めた点）:
//
//   | 由来                  | 数えるか | 当日の判定                                     |
//   | SoftwareStopS1        | 数える   | 承認（＝発動）または約定の時刻が当日          |
//   | ProtectiveStopS0      | 数える   | **約定**の時刻が当日（武装しただけでは数えない）|
//   | ProtectionLostClose   | 数えない | —                                              |
//   | OrderApproved         | 数えない | —（判断由来・owner の手仕舞い・維持率の自動縮小）|
//   | null（記録されていない）| **不明** | 承認または約定の時刻が当日                    |
//
// 当日は**その市場の現地取引日**（TradingDay.Of。米国株は米国東部の暦日。夏時間は TimeZoneInfo が吸収）。
// 同じ方向に「損切り済み」と「不明」が並んだら**損切り済み**を採る（より具体的な理由で拒否する）。
public static class StopOutProjection
{
    /// <summary>
    /// 発注審査が台帳を読むときの下限（現在からの幅）。どの市場の「当日」も取りこぼさない幅であればよく、
    /// 当日の判定そのものは <see cref="Project"/> が市場の現地取引日で行う（走査量の上限にすぎない）。
    /// </summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(2);

    public static StopOutReentrySupply Project(
        IReadOnlyList<LedgerCloseApproval> closes, Market market, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(closes);

        var today = TradingDay.Of(now, market);
        var longSide = StopOutStatus.None;
        var shortSide = StopOutStatus.None;

        foreach (var close in closes)
        {
            // 別市場の同一コードを混ぜない（呼び出し側が絞っているが、純関数としても守る）。
            if (close.Market != market)
                continue;

            var status = Classify(close, market, today);
            if (status == StopOutStatus.None)
                continue;

            // 売りの決済はロング建玉を、買いの決済はショート建玉を閉じた。
            if (close.Side == TradeSide.Sell)
                longSide = Stronger(longSide, status);
            else
                shortSide = Stronger(shortSide, status);
        }

        return new StopOutReentrySupply(longSide, shortSide);
    }

    private static StopOutStatus Classify(LedgerCloseApproval close, Market market, DateOnly today)
    {
        var approvedToday = TradingDay.Of(close.ApprovedAt, market) == today;
        var filledToday = close.FillTimes.Any(t => TradingDay.Of(t, market) == today);

        return close.Source switch
        {
            ApprovalSource.SoftwareStopS1 when approvedToday || filledToday => StopOutStatus.StoppedOut,
            ApprovalSource.ProtectiveStopS0 when filledToday => StopOutStatus.StoppedOut,
            // 🔴 決定6: 由来が記録されていない当日の決済は「損切りではない」と言えない。
            null when approvedToday || filledToday => StopOutStatus.Unknown,
            _ => StopOutStatus.None,
        };
    }

    // 損切り済み ＞ 不明 ＞ 無し。
    private static StopOutStatus Stronger(StopOutStatus current, StopOutStatus candidate) =>
        current == StopOutStatus.StoppedOut || candidate == StopOutStatus.StoppedOut
            ? StopOutStatus.StoppedOut
            : current == StopOutStatus.Unknown || candidate == StopOutStatus.Unknown
                ? StopOutStatus.Unknown
                : StopOutStatus.None;
}
