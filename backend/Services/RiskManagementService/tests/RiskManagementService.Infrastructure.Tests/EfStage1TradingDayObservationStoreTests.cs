using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-20, FR-12, #385, 06_daytrading-review §4.2, IADR-0150:
// 稼働の観測ログ（Stage 1 の営業日数の供給元）の EF ストアを InMemory DB で検証する。
// 算入規則そのものは Stage1SessionUptimeTests が権威。ここは
// **永続化した場合も同じ結果になること**と**プロセス再起動をまたいで積み上がること**・**窓の区切り**を確かめる。
public class EfStage1TradingDayObservationStoreTests
{
    private const int Open = Stage1SessionHypotheses.RegularSessionOpenMinuteOfDayEasternTime;
    private const int RegularClose = Stage1SessionHypotheses.RegularCloseMinuteOfDayEasternTime;

    private static readonly DateOnly Weekday = new(2026, 8, 3);   // 月曜

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static void CreditSession(
        IStage1TradingDayObservationStore store,
        DateOnly date,
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        int from = Open,
        int to = RegularClose)
    {
        for (var minute = from; minute <= to; minute += 30)
        {
            store.CreditUptime(date, provider, minute, 30);
        }
    }

    // 記録が無ければ 0 日。**0 は fail-safe**（期間条件が未充足＝昇格しない）。
    [Fact]
    public void 記録が無ければ0日を返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1TradingDayObservationStore store = new EfStage1TradingDayObservationStore(db);

        store.GetQualifiedTradingDayCount().Should().Be(0);
        store.GetExcludedInternalPaperDayCount().Should().Be(0);
    }

    // 正の確認: 場中を通した稼働が 1 営業日として記録され、別スコープからも同じ結果が読める。
    [Fact]
    public void 場中を通した稼働が営業日として記録され別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            CreditSession(new EfStage1TradingDayObservationStore(db), Weekday);
            CreditSession(new EfStage1TradingDayObservationStore(db), Weekday.AddDays(1));
        }

        using var db2 = NewContext(dbName);
        new EfStage1TradingDayObservationStore(db2).GetQualifiedTradingDayCount().Should().Be(2);
    }

    // **再起動をまたいだ積み上げ**（本ストアが永続である理由そのもの）。
    // 午前だけを記録して落ち、別コンテキスト（＝別プロセス相当）で午後を足すと 1 日として算入される。
    // 分数をメモリに持つ実装だと、再起動のたびに 0 からになり営業日が一生積まれない。
    [Fact]
    public void 別コンテキストで午後を足しても同じ日として積み上がる()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            // 9:30〜12:00（150 分）。半日仮説は満たすが通常日仮説（195 分）に届かない。
            CreditSession(new EfStage1TradingDayObservationStore(db), Weekday, to: 720);
            new EfStage1TradingDayObservationStore(db).GetQualifiedTradingDayCount().Should().Be(0);
        }

        using (var db = NewContext(dbName))
        {
            // 12:00〜16:00 を足すと通算 390 分になる。
            CreditSession(new EfStage1TradingDayObservationStore(db), Weekday, from: 720);
        }

        using var db2 = NewContext(dbName);
        new EfStage1TradingDayObservationStore(db2).GetQualifiedTradingDayCount().Should().Be(1);
    }

    // **否定形**: 同じ観測の再記録（メッセージ再送）で分数が二重に積まれない。
    // (取引日, 発注先) の主キーと純関数の「逆行・同時刻は積まない」規則が担保する。
    [Fact]
    public void 同じ観測の再記録で二重に積まない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1TradingDayObservationStore store = new EfStage1TradingDayObservationStore(db);

        // 9:30〜12:00 を 2 度流す。二重計上されるなら 300 分となり通常日仮説を満たしてしまう。
        CreditSession(store, Weekday, to: 720);
        CreditSession(store, Weekday, to: 720);

        store.GetQualifiedTradingDayCount().Should().Be(0);
        db.Stage1SessionUptimes.Single().OperationalMinutesBeforeRegularClose.Should().Be(150);
    }

    // **否定形**: 内蔵 paper の稼働は算入されず、除外日として別掲される（SC-03）。
    [Fact]
    public void 内蔵paperの稼働日は算入されず除外日として数えられる()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1TradingDayObservationStore store = new EfStage1TradingDayObservationStore(db);

        CreditSession(store, Weekday, BrokerProvider.InternalPaper);

        store.GetQualifiedTradingDayCount().Should().Be(0);
        store.GetExcludedInternalPaperDayCount().Should().Be(1);
    }

    // 同じ日に SIMULATE でも稼働していれば、その日は「除外された日」ではない。
    [Fact]
    public void 同じ日にSIMULATEでも稼働していれば除外日には数えない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1TradingDayObservationStore store = new EfStage1TradingDayObservationStore(db);

        CreditSession(store, Weekday, BrokerProvider.InternalPaper);
        CreditSession(store, Weekday);

        store.GetQualifiedTradingDayCount().Should().Be(1);
        store.GetExcludedInternalPaperDayCount().Should().Be(0);
    }

    // FR-20, §4.2「起算点＝Stage 1 遷移日」: 窓の区切りで観測が消え、直後は 0 日（fail-safe）。
    [Fact]
    public void 窓を区切ると営業日数は0に戻る()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1TradingDayObservationStore store = new EfStage1TradingDayObservationStore(db);

        CreditSession(store, Weekday);
        store.GetQualifiedTradingDayCount().Should().Be(1);

        store.ResetWindow();

        store.GetQualifiedTradingDayCount().Should().Be(0);
        db.Stage1SessionUptimes.Should().BeEmpty();
    }

    // **否定形**: 週末の稼働は永続化されても算入されない（曜日の算術は保存経路でも効く）。
    [Fact]
    public void 週末の稼働は永続化しても算入されない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1TradingDayObservationStore store = new EfStage1TradingDayObservationStore(db);

        CreditSession(store, new DateOnly(2026, 8, 1));  // 土曜

        store.GetQualifiedTradingDayCount().Should().Be(0);
        db.Stage1SessionUptimes.Single().QualifiesTowardStage1.Should().BeFalse();
    }
}
