using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-10, FR-11, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15, #419, IADR-0159:
// 強制買戻しの推定台帳の EF 実装（追記専用）。
//
// **永続でなければならない。** 30 日の空売り禁止をプロセス内に持つと、再起動で禁止が消える（fail-open）。
// 期限は**日付でのみ**失効し、観測の不在・照会の失敗では解けない（IADR-0159 決定4）。
internal sealed class EfBuyInInferenceStore(RiskManagementDbContext db) : IBuyInInferenceStore
{
    public void Append(BuyInInferenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 追記専用（Id は呼び出し側が採番する）。同一 Id の再送は無視する（冪等）。
        if (db.BuyInInferences.Find(record.Id) is not null)
        {
            return;
        }

        db.BuyInInferences.Add(new BuyInInferenceRow
        {
            Id = record.Id,
            Symbol = record.Symbol,
            Market = record.Market,
            LedgerShortQuantity = record.LedgerShortQuantity,
            BrokerShortQuantity = record.BrokerShortQuantity,
            InFlightCloseQuantity = record.InFlightCloseQuantity,
            UnexplainedQuantity = record.UnexplainedQuantity,
            NewlyInferredQuantity = record.NewlyInferredQuantity,
            BanUntil = record.BanUntil,
            InferredOn = record.InferredOn,
            ObservedAtUtc = record.ObservedAt,
            InferredAtUtc = record.InferredAt,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<BuyInInferenceRecord> GetLatestPerSymbol() =>
        db.BuyInInferences
            .AsNoTracking()
            .AsEnumerable()
            .GroupBy(r => (r.Symbol, r.Market))
            .Select(g => g.OrderByDescending(r => r.InferredAtUtc).ThenByDescending(r => r.Id).First())
            .Select(Map)
            .ToList();

    public DateOnly? GetBanUntil(string symbol, Market market) =>
        db.BuyInInferences
            .AsNoTracking()
            .Where(r => r.Symbol == symbol && r.Market == market && r.BanUntil != null)
            .Select(r => r.BanUntil)
            .ToList()
            .DefaultIfEmpty(null)
            .Max();

    public int CountInferredBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        db.BuyInInferences
            .AsNoTracking()
            .Count(r => r.NewlyInferredQuantity > 0 && r.InferredOn >= fromInclusive && r.InferredOn <= toInclusive);

    public IReadOnlyList<BuyInInferenceRecord> GetInferredBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        db.BuyInInferences
            .AsNoTracking()
            .Where(r => r.NewlyInferredQuantity > 0 && r.InferredOn >= fromInclusive && r.InferredOn <= toInclusive)
            .OrderBy(r => r.InferredAtUtc)
            .ToList()
            .Select(Map)
            .ToList();

    private static BuyInInferenceRecord Map(BuyInInferenceRow r) => new(
        r.Id, r.Symbol, r.Market, r.LedgerShortQuantity, r.BrokerShortQuantity, r.InFlightCloseQuantity,
        r.UnexplainedQuantity, r.NewlyInferredQuantity, r.BanUntil, r.InferredOn, r.ObservedAtUtc, r.InferredAtUtc);
}
