using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-11, FR-06, #419, IADR-0159: 強制買戻しの推定台帳のインメモリ実装（ユニット試験・非 relational 用）。
//
// **本番は EF 実装（EfBuyInInferenceStore）である。** 30 日の禁止をプロセス内に持つと再起動で禁止が消える
// （fail-open）ため、本実装を本番の既定にしてはならない。
public sealed class InMemoryBuyInInferenceStore : IBuyInInferenceStore
{
    private readonly Lock _gate = new();
    private readonly List<BuyInInferenceRecord> _records = [];

    public void Append(BuyInInferenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _records.Add(record);
        }
    }

    public IReadOnlyList<BuyInInferenceRecord> GetLatestPerSymbol()
    {
        lock (_gate)
        {
            return _records
                .GroupBy(r => (r.Symbol, r.Market))
                .Select(g => g.OrderByDescending(r => r.InferredAt).ThenByDescending(r => r.Id).First())
                .ToList();
        }
    }

    public DateOnly? GetBanUntil(string symbol, Market market)
    {
        lock (_gate)
        {
            // 期限は記録された BanUntil の**最大値**。リセット行（null）では戻らない。
            return _records
                .Where(r => r.Symbol == symbol && r.Market == market && r.BanUntil is not null)
                .Select(r => r.BanUntil)
                .DefaultIfEmpty(null)
                .Max();
        }
    }

    public int CountInferredBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            return _records.Count(r => IsInferredIn(r, fromInclusive, toInclusive));
        }
    }

    public IReadOnlyList<BuyInInferenceRecord> GetInferredBetween(DateOnly fromInclusive, DateOnly toInclusive)
    {
        lock (_gate)
        {
            return _records
                .Where(r => IsInferredIn(r, fromInclusive, toInclusive))
                .OrderBy(r => r.InferredAt)
                .ToList();
        }
    }

    // リセット行（NewlyInferredQuantity == 0）は**発生回数に数えない**（ADR-0016 決定15 の集計元は推定の件数）。
    private static bool IsInferredIn(BuyInInferenceRecord r, DateOnly from, DateOnly to) =>
        r.NewlyInferredQuantity > 0 && r.InferredOn >= from && r.InferredOn <= to;
}
