using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150: 稼働の観測（BrokerAvailabilityObserved）を購読し、
// 段階ゲートの「60 営業日」を数えるための稼働分数を積む。
//
// **本ハンドラは判定をしない。** 観測を米国東部時間の（取引日, 分）へ写してストアへ渡すだけであり、
// 「その区間を稼働として積むか」も「その日を営業日として算入するか」も純関数（Stage1SessionUptime /
// Stage1SessionHypotheses）が決める。カレンダーも閾値もここには無い。
//
// ADR-0013, IADR-0129 決定10, #354: 再試行では同じ観測が再配送され得るが、積み方の規則が
// 「逆行・同時刻は積まない」であるため件数（分数）は増えない（冪等）。
public sealed class BrokerAvailabilityObservedHandler(
    IStage1TradingDayObservationStore observations,
    ILogger<BrokerAvailabilityObservedHandler> logger)
{
    public void Handle(BrokerAvailabilityObserved message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var (date, minuteOfDay) = EasternTradingDate.SessionMinuteOf(message.ObservedAt);

        // 巡回間隔は「この観測が遡って保証する最大の時間」であり、上限（30 分）は純関数がクランプする。
        // 端数は切り捨てる（切り上げると保証していない分を積む側へ倒れる）。
        var coveredMinutes = (int)message.CoveredInterval.TotalMinutes;

        observations.CreditUptime(date, message.Provider, minuteOfDay, coveredMinutes);

        logger.LogDebug(
            "Stage 1 稼働観測を記録: 取引日(ET)={Date} 分={Minute} 発注先={Provider} 保証={Covered}分",
            date, minuteOfDay, message.Provider, coveredMinutes);
    }
}
