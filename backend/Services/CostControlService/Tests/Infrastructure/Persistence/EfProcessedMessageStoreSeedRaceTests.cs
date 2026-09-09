using AwesomeAssertions;
using CostControlService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CostControlService.Tests;

// NFR（費用）, #714, IADR-0055, IADR-0317, IADR-0319:
// **同じ MessageId を 2 者が同時に処理済みとしてマークする競合**を、順序を固定して再現する。
//
// 後から確定した側は主キー衝突で失敗するが、その例外型は**プロバイダごとに違う**
// （relational は DbUpdateException、EF Core の InMemory は ArgumentException）。
// 例外の型で重複を判定していると、取りこぼした側だけが素通りする（#707 の実測）。
//
// 🔴 本ストアは「重複＝処理済み」を false で表す。**本物の書き込み失敗が false に化けると、
// 呼び出し側（LlmCostIncurredHandler）が二重計上を避けるために no-op で return し、
// 費用が 1 円も計上されないままメッセージが消える**（fail-open）。判定は行の実在で行う。
//
// 本ストアは操作ごとに短命 DbContext を自前で生成する（IADR-0034 / IADR-0055 決定4）ため、
// 交錯の seam は DbContext のイベントではなく **options へ載せた SaveChangesInterceptor** で組み立てる。
public class EfProcessedMessageStoreSeedRaceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static DbContextOptions<CostControlDbContext> NewOptions(
        string dbName, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<CostControlDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options;

    // 後発が Add を stage した後・確定する前に、先発へ 1 回だけ割り込ませる。
    private sealed class InterleaveOnceInterceptor(Action early) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (!Fired)
            {
                Fired = true;
                early();
            }

            return base.SavingChanges(eventData, result);
        }
    }

    // 保存を必ず失敗させる（競合ではない障害の模擬）。行は 1 件も生まれない。
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("保存に失敗した（競合ではない）。");
    }

    // 再現（是正前は赤）: 同じ MessageId を 2 者が同時にマークすると、後発は false（＝二重計上しない）。
    [Fact]
    public void 同一メッセージの同時マークで後発は処理済みとして_false_を返す()
    {
        var dbName = $"processed-{Guid.NewGuid()}";
        var messageId = Guid.NewGuid();
        var interleave = new InterleaveOnceInterceptor(() =>
            new EfProcessedMessageStore(NewOptions(dbName)).TryMarkProcessed(messageId, At));

        var marked = new EfProcessedMessageStore(NewOptions(dbName, interleave))
            .TryMarkProcessed(messageId, At);

        interleave.Fired.Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        marked.Should().BeFalse("他方が先に処理済みとしてマークした＝二重計上を避ける");
    }

    // 陽性対照: 競合が起きなければ従来どおり true（自分が最初にマークした）を返す。
    [Fact]
    public void 競合しなければ自分がマークして_true_を返す()
    {
        var dbName = $"processed-{Guid.NewGuid()}";

        new EfProcessedMessageStore(NewOptions(dbName))
            .TryMarkProcessed(Guid.NewGuid(), At).Should().BeTrue();
    }

    // 否定形: **競合ではない保存失敗は握り潰さない。** 従来は false に化け、呼び出し側が
    // 「処理済み」とみなして return し、費用が計上されないままメッセージが消えていた。
    [Fact]
    public void 行が生まれない保存失敗は握り潰さず送出する()
    {
        var options = NewOptions($"processed-{Guid.NewGuid()}", new ThrowingSaveChangesInterceptor());

        var act = () => new EfProcessedMessageStore(options).TryMarkProcessed(Guid.NewGuid(), At);

        act.Should().Throw<DbUpdateException>();
    }
}
