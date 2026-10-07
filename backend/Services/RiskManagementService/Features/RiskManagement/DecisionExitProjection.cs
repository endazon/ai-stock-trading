using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, #1176, IADR-0495 決定3: 1 銘柄の決済の承認（LedgerCloseApproval）から、**当日の判断由来の決済（利確・判断の手仕舞い）の有無**を
// 建玉の方向ごとに射影する純関数。StopOutProjection（#935 / IADR-0394）と同じ形・同じ入力・同じ当日の判定で、数える由来だけが違う。
// DB・時計に依存しない（now は呼び出し側が与える）。
//
// 数える規則:
//
//   | 由来                     | 数えるか | 当日の判定                                  |
//   | TradeDecision            | 数える   | 承認または約定の時刻が当日（約定を待たない）  |
//   | SoftwareStopS1 / S0      | 数えない | —（StoppedOutSameDay が別の理由で止める）     |
//   | ProtectionLostClose      | 数えない | —                                            |
//   | OrderApproved            | 数えない | —（owner の手仕舞い・維持率の自動縮小。#1176 より前の判断由来もこの値） |
//   | null（記録されていない）  | 数えない | —（StopOutStatusUnknown が既に同じ方向を止める） |
//
// 当日は**その市場の現地取引日**（TradingDay.Of。米国株は米国東部の暦日・夏時間は TimeZoneInfo が吸収、日本株は JST の暦日）。
// 🔴 #1209, IADR-0506: 「当日」と「方向」の規則そのものは共有カーネルの DecisionExitReentry（純関数）に置き、ここは由来・市場で絞って
// 時刻を市場の現地取引日へ写すだけにする。Stage 0 の再生（バックテスト）が同じ述語を通るためである（規則を 2 か所に置かない）。
public static class DecisionExitProjection
{
    public static DecisionExitReentrySupply Project(
        IReadOnlyList<LedgerCloseApproval> closes, Market market, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(closes);

        var exits = closes
            // 別市場の同一コードを混ぜない（呼び出し側が絞っているが、純関数としても守る）。判断由来の決済だけを数える。
            .Where(close => close.Market == market && close.Source == ApprovalSource.TradeDecision)
            // 承認（判断の決済の承認）または約定が当日なら数える（約定を待たない——約定が台帳へ届く前に次の判断の審査が来得る）。
            .Select(close => new DecisionExitOnTradingDays(
                close.Side,
                TradingDay.Of(close.ApprovedAt, market),
                [.. close.FillTimes.Select(t => TradingDay.Of(t, market))]));

        var sides = DecisionExitReentry.Project(exits, TradingDay.Of(now, market));
        return new DecisionExitReentrySupply(sides.LongSide, sides.ShortSide);
    }
}
