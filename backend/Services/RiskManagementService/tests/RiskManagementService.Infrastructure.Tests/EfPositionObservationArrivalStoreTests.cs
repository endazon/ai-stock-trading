using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-21, FR-10, FR-06, #463, IADR-0181: 観測の到達（最終観測時刻・単一行）の EF 永続化を InMemory DB で検証する。
//
// **本ストアの存在理由は「推定台帳では区別できない 2 つの事実を分ける」ことである。**
// 台帳は推定が起きたときにしか行を書かないため、行数 0 は
//   1. 観測が一度も届いていない（＝この統制がまったく働いていない・異常）
//   2. 観測した結果、強制買戻しは 1 件も無かった（＝正常）
// を区別できない。本ストアが 1 と 2 を分ける唯一の手段である。
public class EfPositionObservationArrivalStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 0, 0, 0, TimeSpan.Zero);

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    // **既定は未供給。** ここを既定「観測済み」に倒すと fail-open になる
    //（観測が一度も無い状態で、推定 0 件が「正当な 0」として報告される）。
    [Fact]
    public void 未記録なら最終観測時刻は_null_である()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfPositionObservationArrivalStore(db).GetLastObservedAt().Should().BeNull();
    }

    // 永続でなければならない（プロセス内に持つと再起動で「観測が届いていない」へ戻る）。
    // 別コンテキストは「別レプリカ／再起動」の代理。
    [Fact]
    public void 記録した最終観測時刻は別コンテキストからも読める_durable()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfPositionObservationArrivalStore(db).Record(T0);
        }

        using (var db = NewContext(dbName))
        {
            new EfPositionObservationArrivalStore(db).GetLastObservedAt().Should().Be(T0);
        }
    }

    [Fact]
    public void 新しい観測は最終観測時刻を前進させる()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPositionObservationArrivalStore(db);

        store.Record(T0);
        store.Record(T0.AddMinutes(30));

        store.GetLastObservedAt().Should().Be(T0.AddMinutes(30));
    }

    // **否定形（最重要）**: 後着の古い観測で巻き戻さない。
    // 順序保証の無いバスでは古い観測が後から届き得る。巻き戻すと「供給されていた」状態が
    // 後から未供給寄りへ落ち、報告済みの正当な 0 の根拠が消える。
    [Theory]
    [InlineData(-60)]  // 1 時間前の観測が後から届く
    [InlineData(-1)]   // 1 分前
    [InlineData(0)]    // 同時刻（再送）
    public void 記録済みより古い_または同じ観測では巻き戻さない(int offsetMinutes)
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfPositionObservationArrivalStore(db);
        store.Record(T0);

        store.Record(T0.AddMinutes(offsetMinutes));

        store.GetLastObservedAt().Should().Be(T0);
    }
}
