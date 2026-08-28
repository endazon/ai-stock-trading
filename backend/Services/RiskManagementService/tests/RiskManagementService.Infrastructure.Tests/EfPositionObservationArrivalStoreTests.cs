using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Infrastructure.Tests;

// FR-21, FR-10, FR-06, #463, IADR-0181: 観測が届いた**取引日**の EF 永続化を InMemory DB で検証する。
//
// **［2026-08-08 改定］粒度は取引日の集合である**（計画 FR-21・裁定 planning#292）。
// 従前の単一の「最終観測時刻」は報告期間を観測が覆っていたかを判定できず、
// **初回観測より前の期間や観測が途中で止まった期間が「正当な 0」として報告されていた**。
public class EfPositionObservationArrivalStoreTests
{
    private static readonly DateOnly Day = new(2026, 8, 6);
    private static readonly DateTimeOffset At = new(2026, 8, 6, 3, 0, 0, TimeSpan.Zero);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    // **既定は未観測。** ここを「観測済み」に倒すと fail-open になる。
    [Fact]
    public void 未記録なら観測された取引日は無い()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPositionObservationArrivalStore(db)
            .GetObservedDaysBetween(Day.AddDays(-30), Day.AddDays(30))
            .Should().BeEmpty();
    }

    // 永続でなければならない（再起動で「観測が届いていない」へ戻ると供給が未供給へ化ける）。
    // 別コンテキストは「別レプリカ／再起動」の代理。
    [Fact]
    public void 記録した取引日は別コンテキストからも読める_durable()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfPositionObservationArrivalStore(db).Record(Day, At);
        }

        using (var db = NewContext(dbName))
        {
            new EfPositionObservationArrivalStore(db)
                .GetObservedDaysBetween(Day, Day)
                .Should().ContainSingle().Which.Should().Be(Day);
        }
    }

    // **同一取引日に何度観測が届いても行は増えない**（主キー＝取引日）。
    // 判定に使うのは行の存在であり、件数ではない。
    [Fact]
    public void 同じ取引日の複数観測でも行は増えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPositionObservationArrivalStore(db);

        store.Record(Day, At);
        store.Record(Day, At.AddMinutes(30));
        store.Record(Day, At.AddHours(2));

        store.GetObservedDaysBetween(Day, Day).Should().ContainSingle();
    }

    // **否定形**: 同一取引日で後着の古い（または同じ）観測時刻では `LastObservedAtUtc` を巻き戻さない。
    //
    // `LastObservedAtUtc` は `periodCovered` の判定には使わない**診断用**の値だが、
    // 「その日に最後に観測できたのはいつか」を運用者が読む値であり、**後着の再送で過去へ戻ると
    // 障害の切り分けを誤らせる**。判定に使わないことは、検査しない理由にはならない。
    //
    // 本テストは改定（単一行 → 取引日ごと）で一度失われ、AI レビューの指摘で復元した
    // ——**分岐がどのテストからも到達しなくなっていた**（IADR-0166 が名付けた「緑だが検査されていない」）。
    [Theory]
    [InlineData(-60)]  // 1 時間前の観測が後から届く
    [InlineData(-1)]   // 1 分前
    [InlineData(0)]    // 同時刻（再送）
    public void 同一取引日で古い_または同じ観測時刻では巻き戻さない(int offsetMinutes)
    {
        var dbName = Guid.NewGuid().ToString();
        using (var db = NewContext(dbName))
        {
            var store = new EfPositionObservationArrivalStore(db);
            store.Record(Day, At);
            store.Record(Day, At.AddMinutes(offsetMinutes));
        }

        using (var db = NewContext(dbName))
        {
            db.PositionObservationDays.Single(r => r.TradingDay == Day)
                .LastObservedAtUtc.Should().Be(At);
        }
    }

    // 期間の切り出し（範囲外の日は返さない）。
    [Fact]
    public void 期間外の取引日は返さない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPositionObservationArrivalStore(db);
        store.Record(Day.AddDays(-1), At.AddDays(-1));
        store.Record(Day, At);
        store.Record(Day.AddDays(1), At.AddDays(1));

        store.GetObservedDaysBetween(Day, Day).Should().ContainSingle().Which.Should().Be(Day);
    }

    // **否定形**: 観測が届かなかった日は集合に現れない —— これが「途中で止まった期間」を
    // 未供給へ倒す唯一の根拠である（従前の単一値ではこの区別ができなかった）。
    [Fact]
    public void 観測が届かなかった日は集合に現れない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPositionObservationArrivalStore(db);
        store.Record(Day, At);
        // 翌日は観測が届かなかった（ブローカ接続の障害等）。
        store.Record(Day.AddDays(2), At.AddDays(2));

        store.GetObservedDaysBetween(Day, Day.AddDays(2))
            .Should().BeEquivalentTo([Day, Day.AddDays(2)]);
    }
}
