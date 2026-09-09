using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using ConfigurationService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ConfigurationService.Tests;

// FR-17, #714, IADR-0317, IADR-0319: **全体前提条件の単一行を 2 者が同時に初回シードする競合**を、
// 順序を固定して再現する。
//
// 後から確定した側は一意キー違反で失敗するが、その例外型は**プロバイダごとに違う**
// （relational は DbUpdateException、EF Core の InMemory は ArgumentException
// 「An item with the same key has already been added」）。例外の型で競合を判定していると、
// 取りこぼした側だけが素通りしてエンドポイントの例外フィルタで 400（＝利用者の要求が悪い、という嘘）
// へ写像される（#707 の実測）。
//
// 時間に依存させると再現しないので、DbContext.SavingChanges を seam にして交錯を決定的に組み立てる。
public class EfAssumptionsStoreSeedRaceTests
{
    private static ConfigurationDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options);

    // 後発が Add を stage した後・確定する前に、先発（別コンテキスト）へ 1 回だけ割り込ませる。
    private static Func<bool> InterleaveOnce(
        ConfigurationDbContext late, string dbName, Action<ConfigurationDbContext> early)
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

    // 保存を必ず失敗させる（競合ではない障害の模擬）。行は 1 件も生まれない。
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("保存に失敗した（競合ではない）。");
    }

    // 先発が「既定とは違う値」でシード済みの行を作る（後発が読み直したことを判別可能にする）。
    private static void SeedDistinguishable(ConfigurationDbContext db)
    {
        var store = new EfAssumptionsStore(db);
        var current = store.GetCurrent();
        store.Save(current.Assumptions with { CapitalGainsTaxRate = 0.5m }, current.Version);
    }

    // 再現（是正前は赤）: 後発は例外を投げず、**先発が書いた行**を読み直して返す。
    [Fact]
    public void 前提条件の同時初回シードで後発は先発の行を読み直す()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, SeedDistinguishable);

        var current = new EfAssumptionsStore(late).GetCurrent();

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        current.Assumptions.CapitalGainsTaxRate.Should().Be(0.5m, "後発は先発が書いた行を読み直す");
        current.Version.Should().Be(2, "先発が確定させた版がそのまま返る");
    }

    // 陽性対照: 競合が起きなければ従来どおり自分がシードした既定値（Version 1）を返す。
    [Fact]
    public void 前提条件は競合しなければ自分がシードした既定値を返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());

        var current = new EfAssumptionsStore(db).GetCurrent();

        current.Version.Should().Be(1);
        current.Assumptions.CapitalGainsTaxRate
            .Should().Be(TradingAssumptionsDefaults.Create().CapitalGainsTaxRate);
    }

    // 否定形: **競合ではない保存失敗は握り潰さない。** 未永続の既定値を返して黙ると
    // 「保存できていないのに既定の前提条件で動く」状態を静かに作る。
    [Fact]
    public void 前提条件は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfAssumptionsStore(db).GetCurrent();

        act.Should().Throw<DbUpdateException>();
    }

    // 再現（Save 経路・是正前は赤）: 未シード状態で同時に PUT が来ても、後発は先発の行を使って続行する。
    [Fact]
    public void 前提条件の保存は同時初回シードでも先発の行を使って続行する()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);
        var interleaved = InterleaveOnce(late, dbName, early =>
            new EfAssumptionsStore(early).GetCurrent());

        // 先発が Version 1 でシードした行に対し、後発は expectedVersion=1 で保存できる。
        var version = new EfAssumptionsStore(late)
            .Save(TradingAssumptionsDefaults.Create() with { CapitalGainsTaxRate = 0.3m }, expectedVersion: 1);

        interleaved().Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        version.Should().Be(2);
    }

    // 否定形（Save 経路）: 行が生まれない保存失敗は「シードに失敗しました」という別の話へ化けさせず、
    // 原因（何が保存を失敗させたのか）をそのまま上げる。
    [Fact]
    public void 前提条件の保存は行が生まれない保存失敗を握り潰さず送出する()
    {
        using var db = NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor());

        var act = () => new EfAssumptionsStore(db).Save(TradingAssumptionsDefaults.Create(), expectedVersion: 1);

        act.Should().Throw<DbUpdateException>();
    }
}
