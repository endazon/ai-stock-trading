using AuditService.Application.Adapters;
using AuditService.Application.State;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Application.Tests;

// FR-06, FR-11, #381, IADR-0019, IADR-0199 決定2・決定3: 種別 × 期間の照会（日報の集計が引く経路）。
//
// 🔴 **本テストが守っているのは「取りこぼさないこと」である。**
// 既存の照会は相関単位か直近 N 件しか無く、**上限つき取得で代用すると期間内の件数が上限を超えたときに
// 古いものから静かに落ちる**——落ちても赤くならないため、報告書だけが静かに間違う。
public class AuditEventStorePeriodQueryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static AuditEntry Entry(string eventType, DateTimeOffset occurredAt) => new(
        Guid.NewGuid(), eventType, Guid.NewGuid(), "USD", "要約", "{}", occurredAt, occurredAt);

    private static InMemoryAuditEventStore StoreWith(params AuditEntry[] entries)
    {
        var store = new InMemoryAuditEventStore();
        foreach (var e in entries) store.Append(e);
        return store;
    }

    [Fact]
    public void 指定した種別だけを時系列で返す()
    {
        var store = StoreWith(
            Entry("FxRateStale", T0.AddHours(2)),
            Entry("FxRateStale", T0.AddHours(1)),
            Entry("OrderExecuted", T0.AddHours(1)));

        var result = store.GetByTypesInPeriod(["FxRateStale"], T0, T0.AddDays(1));

        result.Should().HaveCount(2);
        result.Should().OnlyContain(e => e.EventType == "FxRateStale");
        result[0].OccurredAt.Should().BeBefore(result[1].OccurredAt, "時系列（昇順）で返す");
    }

    // 🔴 **半開区間の要（IADR-0199 決定3）。** 終端を閉じる実装（`<=`）だと**始端の 1 件が二重に**、
    // 開きすぎる実装（`>`）だと**始端の 1 件が落ちる**。両端を明示的に固定する。
    [Fact]
    public void 始端は含み_終端は含まない()
    {
        var from = T0;
        var to = T0.AddDays(1);
        var store = StoreWith(
            Entry("FxRateStale", from),                        // 始端ちょうど → 含む
            Entry("FxRateStale", to.AddTicks(-1)),             // 終端の直前 → 含む
            Entry("FxRateStale", to));                         // 終端ちょうど → 含まない

        var result = store.GetByTypesInPeriod(["FxRateStale"], from, to);

        result.Should().HaveCount(2, "[from, to) の半開区間である");
        result.Should().NotContain(e => e.OccurredAt == to);
    }

    // 🔴 **その日の最後の 1 秒を落とさない。** 終端を `23:59:59` で閉じる実装だと
    // **23:59:59.5 の事象が翌日の報告書にも当日の報告書にも出ない**（どこにも出ない）。
    [Fact]
    public void 終端直前のミリ秒も取りこぼさない()
    {
        var to = T0.AddDays(1);
        var store = StoreWith(Entry("FxRateStale", to.AddMilliseconds(-1)));

        store.GetByTypesInPeriod(["FxRateStale"], T0, to)
            .Should().ContainSingle("23:59:59.999 は当日である");
    }

    // **否定形**: 期間外は返らない。
    [Fact]
    public void 期間外の記録は返さない()
    {
        var store = StoreWith(
            Entry("FxRateStale", T0.AddDays(-1)),
            Entry("FxRateStale", T0.AddDays(2)));

        store.GetByTypesInPeriod(["FxRateStale"], T0, T0.AddDays(1)).Should().BeEmpty();
    }

    // 🔴 **否定形（最重要）**: 種別が空なら「該当なし」であり「すべて」ではない。
    // ここを全件取得にすると、**呼び出し側の絞り込み漏れが台帳の全量取得に化ける。**
    [Fact]
    public void 種別が空なら_全件ではなく空を返す()
    {
        var store = StoreWith(Entry("FxRateStale", T0), Entry("OrderExecuted", T0));

        store.GetByTypesInPeriod([], T0, T0.AddDays(1)).Should().BeEmpty();
    }

    // 複数種別をまとめて引ける（日報は 4 種を 1 回で引く）。
    [Fact]
    public void 複数の種別をまとめて引ける()
    {
        var store = StoreWith(
            Entry("FxRateStale", T0.AddHours(1)),
            Entry("FxRateSourceFellBack", T0.AddHours(2)),
            Entry("OrderExecuted", T0.AddHours(3)));

        var result = store.GetByTypesInPeriod(["FxRateStale", "FxRateSourceFellBack"], T0, T0.AddDays(1));

        result.Should().HaveCount(2);
        result.Should().NotContain(e => e.EventType == "OrderExecuted");
    }

    // 🔴 **上限を持たないこと。** `GetRecent` の既定（100）で代用していないことを実測で固定する。
    [Fact]
    public void 件数の上限を持たない_100件を超えても全件返す()
    {
        var store = new InMemoryAuditEventStore();
        for (var i = 0; i < 250; i++)
            store.Append(Entry("FxRateStale", T0.AddMinutes(i)));

        store.GetByTypesInPeriod(["FxRateStale"], T0, T0.AddDays(1))
            .Should().HaveCount(250, "期間の集計であり、上限で切ると取りこぼしが静かに起きる");
    }
}
