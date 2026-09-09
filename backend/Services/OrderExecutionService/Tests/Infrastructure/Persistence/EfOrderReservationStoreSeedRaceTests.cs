using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-05, #131, #141, #714, IADR-0057, IADR-0074, IADR-0317, IADR-0319:
// **同じ DecisionId の予約を 2 者が同時に確保／解放する競合**を、順序を固定して再現する。
//
// 後から確定した側は一意キー違反で失敗するが、その例外型は**プロバイダごとに違う**
// （relational は DbUpdateException、EF Core の InMemory は ArgumentException）。
// 例外の型で競合を判定していると、取りこぼした側だけが素通りする（#707 の実測）。
//
// 時間に依存させると再現しないので、DbContext.SavingChanges を seam にして交錯を決定的に組み立てる。
public class EfOrderReservationStoreSeedRaceTests
{
    private static OrderExecutionDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<OrderExecutionDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options);

    // 後発が変更を stage した後・確定する前に、先発（別コンテキスト）へ 1 回だけ割り込ませる。
    private static Func<bool> InterleaveOnce(
        OrderExecutionDbContext late, string dbName, Action<OrderExecutionDbContext> early)
    {
        var fired = false;
        late.SavingChanges += (_, _) =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            using var earlyDb = NewContext(dbName);
            early(earlyDb);
        };

        return () => fired;
    }

    // 保存を必ず失敗させる（競合ではない障害の模擬）。行は 1 件も生まれない／消えない。
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("保存に失敗した（競合ではない）。");
    }

    // 再現（是正前は赤）: 同じ DecisionId を 2 者が同時に予約すると、後発は false（＝発注しない）を返す。
    [Fact]
    public void 予約の同時確保で後発は確保できない()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfOrderReservationStore(early).TryReserve(decisionId, DateTimeOffset.UtcNow.AddSeconds(-1)));

        var reserved = new EfOrderReservationStore(late).TryReserve(decisionId, DateTimeOffset.UtcNow);

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        reserved.Should().BeFalse("他プロセスが先に予約を確保した＝二重発注しない側へ倒す");
    }

    // 陽性対照: 競合が無ければ予約を確保できる（true）。
    [Fact]
    public void 予約は競合しなければ確保できる()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        new EfOrderReservationStore(db).TryReserve(Guid.NewGuid(), DateTimeOffset.UtcNow).Should().BeTrue();
    }

    // 否定形: **競合ではない保存失敗は握り潰さない。** 従来は false に化け、呼び出し側が
    // 「既に予約されている」という嘘の説明（OrderDispatchReservationConflictException）で終わっていた。
    [Fact]
    public void 予約は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfOrderReservationStore(db).TryReserve(Guid.NewGuid(), DateTimeOffset.UtcNow);

        act.Should().Throw<DbUpdateException>();
    }

    // 再現（解放経路・是正前は赤）: 削除の競合は「消したかった行が消えているか」で判定する。
    // 先発が先に解放し切っていれば、後発は例外ではなく false（自分は解放していない）を返す。
    [Fact]
    public void 予約解放の競合では後発は解放していないを返す()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using (var seed = NewContext(dbName))
        {
            new EfOrderReservationStore(seed).TryReserve(decisionId, DateTimeOffset.UtcNow);
        }

        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfOrderReservationStore(early).Release(decisionId));

        var released = new EfOrderReservationStore(late).Release(decisionId);

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        released.Should().BeFalse("先発が解放し切っている＝自分は解放していない");

        using var verify = NewContext(dbName);
        new EfOrderReservationStore(verify).Find(decisionId).Should().BeNull();
    }

    // 陽性対照: 競合が無ければ解放できる（true）。
    [Fact]
    public void 予約は競合しなければ解放できる()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using (var seed = NewContext(dbName))
        {
            new EfOrderReservationStore(seed).TryReserve(decisionId, DateTimeOffset.UtcNow);
        }

        using var db = NewContext(dbName);
        new EfOrderReservationStore(db).Release(decisionId).Should().BeTrue();
    }

    // 否定形（解放経路）: **行がまだ残っている保存失敗は握り潰さない。**
    // 「解放していない」を返して黙ると、滞留 Reserved が誰にも気づかれないまま残る。
    [Fact]
    public void 予約解放は行が残ったままの保存失敗を握り潰さず送出する()
    {
        var dbName = Guid.NewGuid().ToString();
        var decisionId = Guid.NewGuid();
        using (var seed = NewContext(dbName))
        {
            new EfOrderReservationStore(seed).TryReserve(decisionId, DateTimeOffset.UtcNow);
        }

        using var db = NewContext(dbName, new ThrowingSaveChangesInterceptor());

        var act = () => new EfOrderReservationStore(db).Release(decisionId);

        act.Should().Throw<DbUpdateException>();
    }
}
