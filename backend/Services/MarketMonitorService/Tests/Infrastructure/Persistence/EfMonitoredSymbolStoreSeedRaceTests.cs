using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MarketMonitorService.Domain;
using MarketMonitorService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-13, #707, IADR-0317: **設定の単一行を 2 者が同時に初回シードする競合**を、
// 順序を固定して再現する。
//
// 本番での交錯はこうである —— `MonitorPollingService` の 1 回目の巡回（ホスト起動直後に走る）と、
// 利用者の設定更新 HTTP 要求が、**どちらも「行が無い」を観測してから** `SaveChanges` する。
// 後から確定した側は一意キー違反で失敗し、EF Core InMemory ではそれが `ArgumentException` になる。
// `MonitorSettingsEndpoints` のグループ例外フィルタは `ArgumentException` を検証エラーとみなすため、
// **利用者の正しい要求が 400 になる**（MSP#858 で観測された不定期の赤の正体）。
//
// 時間に依存させると再現しないので、`DbContext.SavingChanges` を seam にして交錯を決定的に組み立てる。
// 通るのは本番コード（`EfMonitoredSymbolStore.GetSettings`）そのものである。
public class EfMonitoredSymbolStoreSeedRaceTests
{
    private static MarketMonitorDbContext NewContext(string dbName, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<MarketMonitorDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(interceptors)
            .Options);

    private static MonitorSeedOptions SeedOptionsWith(string symbol) => new()
    {
        SeedSymbols = [new MonitorSeedOptions.SeedSymbolEntry { Symbol = symbol, Market = Market.UnitedStates }],
    };

    // 再現テスト（是正前は赤）: 後発は例外を投げず、**先発が書いた行**を読み直して返す。
    [Fact]
    public void 同時初回シードで後発は先発の行を読み直す()
    {
        var dbName = Guid.NewGuid().ToString();
        using var late = NewContext(dbName);

        // 「後発が Add を stage した後・確定する前に、先発が同じ行を確定させる」交錯を作る。
        var earlyHasSeeded = false;
        late.SavingChanges += (_, _) =>
        {
            if (earlyHasSeeded)
            {
                return;
            }

            earlyHasSeeded = true;
            using var early = NewContext(dbName);
            new EfMonitoredSymbolStore(early, SeedOptionsWith("NVDA")).GetSettings();
        };

        var settings = new EfMonitoredSymbolStore(late).GetSettings();

        earlyHasSeeded.Should().BeTrue("交錯が組み立てられていなければ本テストは何も検証していない");
        // 後発が読み直した値であること＝先発（NVDA でシードした側）の行である。
        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "NVDA");
    }

    // 陽性対照: 競合が起きなければ従来どおり自分がシードした値を返す（交錯の有無で結果が変わることを示す）。
    [Fact]
    public void 競合しなければ自分がシードした値を返す()
    {
        var store = new EfMonitoredSymbolStore(
            NewContext(Guid.NewGuid().ToString()), SeedOptionsWith("AAPL"));

        var settings = store.GetSettings();

        settings.MonitoredSymbols.Should().ContainSingle(s => s.Symbol == "AAPL");
        settings.MovementThresholdRatio.Should().Be(MonitorDefaults.MovementThresholdRatio);
    }

    // 否定形: **競合ではない保存失敗は握り潰さない。** 行が生まれていないのに SaveChanges が失敗
    // したら、未永続の既定値を返して黙るのではなく例外をそのまま上げる
    //（黙ると「保存できていないのに既定値で監視が動く」状態を静かに作る）。
    [Fact]
    public void 行が生まれない保存失敗は握り潰さず送出する()
    {
        var store = new EfMonitoredSymbolStore(
            NewContext(Guid.NewGuid().ToString(), new ThrowingSaveChangesInterceptor()));

        var act = () => store.GetSettings();

        act.Should().Throw<DbUpdateException>();
    }

    // 保存を必ず失敗させる（競合ではない障害の模擬）。行は 1 件も生まれない。
    private sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new DbUpdateException("保存に失敗した（競合ではない）。");
    }
}
