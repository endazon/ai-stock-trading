using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-20, FR-12, #385, IADR-0150: Stage 1 の稼働観測ログのインメモリ実装（ユニット試験・ローカル実行用）。
// 本番配線は EfStage1TradingDayObservationStore。未記録＝0 日＝昇格しない安全側。
public sealed class InMemoryStage1TradingDayObservationStore : IStage1TradingDayObservationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(DateOnly Date, BrokerProvider Provider), Stage1SessionUptime> _uptimes = [];

    public void CreditUptime(
        DateOnly sessionDateEasternTime,
        BrokerProvider provider,
        int observedMinuteOfDayEasternTime,
        int coveredMinutes)
    {
        lock (_gate)
        {
            var key = (sessionDateEasternTime, provider);
            var current = _uptimes.TryGetValue(key, out var existing)
                ? existing
                : new Stage1SessionUptime(sessionDateEasternTime, provider);

            // 積み方の規則（初回は遡らない・落とした区間は積まない・逆行は無視）は純関数が単一情報源。
            _uptimes[key] = current.Credit(observedMinuteOfDayEasternTime, coveredMinutes);
        }
    }

    public int GetQualifiedTradingDayCount()
    {
        lock (_gate)
        {
            return Stage1SessionHypotheses.CountQualifiedDays(_uptimes.Values);
        }
    }

    public int GetExcludedInternalPaperDayCount()
    {
        lock (_gate)
        {
            return Stage1SessionHypotheses.CountExcludedInternalPaperDays(_uptimes.Values);
        }
    }

    // FR-06, FR-20, #569, IADR-0271: 報告書への供給（期間の稼働率）。
    public IReadOnlyList<Stage1SessionUptime> GetSessionUptimesBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        if (fromInclusive > toInclusive)
        {
            return [];
        }

        lock (_gate)
        {
            return
            [
                .. _uptimes.Values
                    .Where(u => u.SessionDateEasternTime >= fromInclusive && u.SessionDateEasternTime <= toInclusive),
            ];
        }
    }

    public void ResetWindow()
    {
        lock (_gate)
        {
            _uptimes.Clear();
        }
    }
}
