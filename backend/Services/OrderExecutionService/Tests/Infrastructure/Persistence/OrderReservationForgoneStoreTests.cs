using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Infrastructure.Persistence;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-828・T-10-829, FR-05, FR-10, #876, IADR-0398: 見送りの記録（Forgone）の意味論を
// **本番の DB 実装とインメモリ実装の両方で同一に**固定する（片側だけの乖離を検知する。#848 と同じ作法）。
//
// EF 側は呼び出しごとに**別のコンテキスト**で開く（別プロセス・次の配送に相当）。戻った時点で記録が
// コミットされていなければ、次のコンテキストからは見えず、再配送の抑止が効かない。
public class OrderReservationForgoneStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Implementations() => ["ef", "inmemory"];

    // 呼ぶたびに store を返す（EF は毎回新しいコンテキスト、インメモリは同じインスタンス）。
    private static Func<IOrderReservationStore> Stores(string kind)
    {
        if (kind == "inmemory")
        {
            var shared = new InMemoryOrderReservationStore();
            return () => shared;
        }

        var dbName = Guid.NewGuid().ToString();
        return () => new EfOrderReservationStore(new OrderExecutionDbContext(
            new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options));
    }

    // T-10-828: 行が無ければ Forgone で挿入し、別のコンテキストからも見える。2 度目は冪等で時刻を動かさない。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 予約前の見送りは行を作り2度目は冪等で時刻を動かさない(string kind)
    {
        var store = Stores(kind);
        var id = Guid.NewGuid();

        store().TryRecordForgone(id, Now).Should().Be(ForgoneRecordOutcome.Recorded);
        store().TryRecordForgone(id, Now.AddMinutes(5)).Should().Be(ForgoneRecordOutcome.AlreadyForgone);
        store().MarkReservationForgone(id, Now.AddMinutes(6)).Should().Be(ForgoneRecordOutcome.AlreadyForgone);

        var row = store().Find(id)!;
        row.State.Should().Be(OrderDispatchState.Forgone);
        row.CompletedAt.Should().Be(Now, "最初に記録した時刻のまま（単調）");
        row.ReservedAt.Should().Be(Now);
        row.BrokerOrderId.Should().BeNull();
    }

    // T-10-828: 自分が取った Reserved は Forgone へ移る（削除しない）。同じ DecisionId は予約を取り直せない。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 自分の予約は見送りへ移り同じDecisionIdは予約を取り直せない(string kind)
    {
        var store = Stores(kind);
        var id = Guid.NewGuid();
        store().TryReserve(id, Now.AddSeconds(-3)).Should().BeTrue();

        store().MarkReservationForgone(id, Now).Should().Be(ForgoneRecordOutcome.Recorded);

        var row = store().Find(id)!;
        row.State.Should().Be(OrderDispatchState.Forgone);
        row.ReservedAt.Should().Be(Now.AddSeconds(-3), "予約を取った時刻は残す");
        row.CompletedAt.Should().Be(Now);
        store().TryReserve(id, Now.AddSeconds(10)).Should().BeFalse("見送った DecisionId で予約を取り直させない（再配送の発注を止める）");
    }

    // T-10-828: 行が無いまま MarkReservationForgone が呼ばれても、見送りを記録する（予約前の見送りと同じ）。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 予約が無いときの見送りへの移行は記録を作る(string kind)
    {
        var store = Stores(kind);
        var id = Guid.NewGuid();

        store().MarkReservationForgone(id, Now).Should().Be(ForgoneRecordOutcome.Recorded);

        store().Find(id)!.State.Should().Be(OrderDispatchState.Forgone);
    }

    // 🔴 T-10-828（否定形・Principle A）: 別の配送の Reserved（送ったか不明）には、予約前の見送りは**触れない**。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 予約前の見送りは他の配送のReservedを書き換えない_否定形(string kind)
    {
        var store = Stores(kind);
        var id = Guid.NewGuid();
        store().TryReserve(id, Now.AddSeconds(-1)).Should().BeTrue();

        store().TryRecordForgone(id, Now).Should().Be(ForgoneRecordOutcome.HeldByReservation);

        var row = store().Find(id)!;
        row.State.Should().Be(OrderDispatchState.Reserved);
        row.CompletedAt.Should().BeNull();
    }

    // 🔴 T-10-829（否定形）: Completed（発注済み）は、どちらの口でも**見送りへ書き換えない**。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 確定済みの予約はどちらの口でも見送りへ書き換えない_否定形(string kind)
    {
        var store = Stores(kind);
        var id = Guid.NewGuid();
        store().TryReserve(id, Now.AddMinutes(-2));
        store().MarkCompleted(id, "BRK-9", Now.AddMinutes(-1));

        store().TryRecordForgone(id, Now).Should().Be(ForgoneRecordOutcome.AlreadyCompleted);
        store().MarkReservationForgone(id, Now).Should().Be(ForgoneRecordOutcome.AlreadyCompleted);

        var row = store().Find(id)!;
        row.State.Should().Be(OrderDispatchState.Completed);
        row.BrokerOrderId.Should().Be("BRK-9");
        row.CompletedAt.Should().Be(Now.AddMinutes(-1));
    }

    // 🔴 T-10-828: Forgone は突合（滞留 Reserved の走査）にも保持期間パージにも載らず、解放（Release）でも消えない。
    // 消えると同じ承認の再配送が予約を取り直して発注できる（#876 の穴が戻る）。
    [Theory]
    [MemberData(nameof(Implementations))]
    public void 見送りの記録は突合にもパージにも載らず解放でも消えない(string kind)
    {
        var store = Stores(kind);
        var id = Guid.NewGuid();
        store().TryRecordForgone(id, Now.AddDays(-400)).Should().Be(ForgoneRecordOutcome.Recorded);

        store().FindStalledReserved(Now, batchSize: 50).Should().BeEmpty();
        store().PurgeCompletedBefore(Now, batchSize: 50).Should().Be(0);
        store().Release(id).Should().BeFalse();
        store().Find(id)!.State.Should().Be(OrderDispatchState.Forgone);
    }

    // 🔴 T-10-829（否定形・EF のみ）: 未定義の状態値の行は「発注済み」の側へ倒す（見送りを主張させない）。
    // 列は整数なので、将来足された状態を知らない版が読むとこの形になる。
    [Fact]
    public void 未定義の状態の行には見送りを主張させない_否定形()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<OrderExecutionDbContext>().UseInMemoryDatabase(dbName).Options;
        var id = Guid.NewGuid();
        using (var seed = new OrderExecutionDbContext(options))
        {
            seed.DispatchReservations.Add(new OrderDispatchReservationRow
            {
                DecisionId = id,
                State = (OrderDispatchState)99,
                ReservedAt = Now.AddMinutes(-1),
            });
            seed.SaveChanges();
        }

        using var db = new OrderExecutionDbContext(options);
        var store = new EfOrderReservationStore(db);

        store.TryRecordForgone(id, Now).Should().Be(ForgoneRecordOutcome.AlreadyCompleted);
        store.MarkReservationForgone(id, Now).Should().Be(ForgoneRecordOutcome.AlreadyCompleted);
        store.Find(id)!.State.Should().Be((OrderDispatchState)99, "知らない状態を上書きしない");
    }
}
