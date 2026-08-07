using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;

// FR-19, FR-10, FR-11, #425, ADR-0025 決定2, IADR-0166: GFV 自前計数台帳の EF 実装（追記専用）。
//
// **永続でなければならない。** 違反記録をプロセス内に持つと再起動で消え、「2 件で新規建てを止める」統制が
// **再起動 1 回で解ける**（fail-open）。口座種別の観測（IADR-0153 決定3・非永続）と設計が違うのは
// 「集計 vs 現在値」の違いである——違反件数は再観測で復元できない履歴である。
internal sealed class EfGoodFaithViolationStore(RiskManagementDbContext db) : IGoodFaithViolationStore
{
    public void Append(GoodFaithViolationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 冪等: 同一注文の 2 度目以降（部分約定の進行・メッセージ再送）は無視する。**件数が増えない側**である。
        if (db.GoodFaithViolations.Find(record.OrderId) is not null)
        {
            return;
        }

        db.GoodFaithViolations.Add(new GoodFaithViolationRow
        {
            OrderId = record.OrderId,
            Id = record.Id,
            DecisionId = record.DecisionId,
            Symbol = record.Symbol,
            Market = record.Market,
            PurchaseAmountInBase = record.PurchaseAmountInBase,
            SettledCashInBase = record.SettledCashInBase,
            OccurredOn = record.OccurredOn,
            ExecutedAtUtc = record.ExecutedAt,
            RecordedAtUtc = record.RecordedAt,
        });
        db.SaveChanges();
    }

    // **0 行でも「0 件を数えた」と返す**（台帳が権威である）。未供給（null）は本ストアが結線されていない
    // ときだけ生じ、その場合は判定コアが現金口座の新規建てを止める（IADR-0166 決定2）。
    //
    // **失効期間は設けない**（累計）。計画は「違反記録の失効」の期間も手段も定義しておらず、
    // 自動失効は fail-open であるため実装で値を発明しない（IADR-0166 決定4）。
    public GoodFaithViolationTally GetTally() =>
        GoodFaithViolationTally.Observed(db.GoodFaithViolations.AsNoTracking().Count());

    public IReadOnlyList<GoodFaithViolationRecord> GetRecordedBetween(DateOnly fromInclusive, DateOnly toInclusive) =>
        db.GoodFaithViolations
            .AsNoTracking()
            .Where(r => r.OccurredOn >= fromInclusive && r.OccurredOn <= toInclusive)
            .OrderBy(r => r.RecordedAtUtc)
            .ToList()
            .Select(Map)
            .ToList();

    private static GoodFaithViolationRecord Map(GoodFaithViolationRow r) => new(
        r.Id, r.OrderId, r.DecisionId, r.Symbol, r.Market, r.PurchaseAmountInBase, r.SettledCashInBase,
        r.OccurredOn, r.ExecutedAtUtc, r.RecordedAtUtc);
}
