using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-11, #465, ADR-0027, IADR-0183: 借株料の日次計上のインメモリ実装。
//
// **本番では使わない。** 累計は建玉の生涯にわたって積み上がる値であり、プロセス内に持つと
// 再起動で費用が消える（実費より小さい累計を「正しい累計」として報告してしまう）。本番は EF 実装を配線する。
public sealed class InMemoryBorrowFeeAccrualStore : IBorrowFeeAccrualStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Symbol, Market Market, DateOnly Day), BorrowFeeAccrual> _accruals = [];
    private readonly Dictionary<(string Symbol, Market Market, DateOnly Day), BorrowFeeUnavailableDay> _unavailable = [];

    public bool Record(BorrowFeeAccrual accrual)
    {
        ArgumentNullException.ThrowIfNull(accrual);

        lock (_gate)
        {
            // 既に同じ日の行があれば**書き換えない**（最初の計上を正とする。冪等）。
            return _accruals.TryAdd((accrual.Symbol, accrual.Market, accrual.TradingDay), accrual);
        }
    }

    public bool RecordUnavailable(BorrowFeeUnavailableDay day)
    {
        ArgumentNullException.ThrowIfNull(day);

        lock (_gate)
        {
            return _unavailable.TryAdd((day.Symbol, day.Market, day.TradingDay), day);
        }
    }

    public IReadOnlyList<BorrowFeeAccrual> GetAccrualsBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            return [.. _accruals.Values
                .Where(a => a.TradingDay >= fromInclusive && a.TradingDay <= toInclusive)
                .OrderBy(a => a.TradingDay)
                .ThenBy(a => a.Symbol, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<BorrowFeeUnavailableDay> GetUnavailableDaysBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            return [.. _unavailable.Values
                .Where(d => d.TradingDay >= fromInclusive && d.TradingDay <= toInclusive)
                .OrderBy(d => d.TradingDay)
                .ThenBy(d => d.Symbol, StringComparer.Ordinal)];
        }
    }
}
