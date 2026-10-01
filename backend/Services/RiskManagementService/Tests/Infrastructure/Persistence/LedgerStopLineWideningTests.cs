using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, ADR-0049, #1136, IADR-0472 決定6: 発注執行が S1 の損切りラインを下限まで遡及して広げた事実（SoftwareStopLineWidened）に、
// 取引台帳の承認行のラインを**広げる向きにだけ**追随させる（市場監視は台帳のラインで到達を出す）。
public class LedgerStopLineWideningTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);

    private static OrderIntent Open(TradeSide side, decimal? stopLoss, string symbol = "NVDA") =>
        new(symbol, Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 230.82m,
            PositionEffect.Open, stopLoss);

    public static TheoryData<string> Stores => new() { "ef", "memory" };

    private static IPortfolioLedgerStore NewStore(string kind, string dbName) => kind == "ef"
        ? new EfPortfolioLedgerStore(new RiskManagementDbContext(
            new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(dbName).Options))
        : new InMemoryPortfolioLedgerStore();

    // T-10-1929, FR-10, IADR-0472 決定6: 広げる向きにだけ書き換え、約定（建玉の射影の入力）のラインが追随する。
    // 狭める・同じ・ライン未記録（null）・決済の承認・方向違い・承認なしは何もしない。
    [Theory]
    [MemberData(nameof(Stores))]
    public void T_10_1929_台帳の損切りラインは広げる向きにだけ追随する(string kind)
    {
        var dbName = Guid.NewGuid().ToString();
        var ledger = NewStore(kind, dbName);
        var longId = Guid.NewGuid();
        var shortId = Guid.NewGuid();
        var unknownId = Guid.NewGuid();
        var closeId = Guid.NewGuid();
        ledger.AppendApproval(longId, Open(TradeSide.Buy, 226.52m), At);
        ledger.AppendFill(longId, "o-long", 10, 230.82m, At);
        ledger.AppendApproval(shortId, Open(TradeSide.Sell, 101m, "TSLA"), At);
        ledger.AppendApproval(unknownId, Open(TradeSide.Buy, null, "AAPL"), At);
        ledger.AppendApproval(closeId, Open(TradeSide.Sell, 240m) with { PositionEffect = PositionEffect.Close }, At);

        ledger.WidenStopLoss(longId, TradeSide.Buy, 226.2036m).Should().BeTrue();
        ledger.WidenStopLoss(longId, TradeSide.Buy, 226.2036m).Should().BeFalse("同じ値は書かない（冪等）");
        ledger.WidenStopLoss(longId, TradeSide.Buy, 226.40m).Should().BeFalse("狭めない（順序の入れ替わり）");
        // #1136 独立監査 F5: 売りの向きで見れば 230 は今のライン 226.2036 より広い（高い）ので、広げる向きの判定では止まらない。
        // 止めるのは方向の検査だけ（方向を見ないと、買い建てのラインを建値の上へ動かす）。
        ledger.WidenStopLoss(longId, TradeSide.Sell, 230m).Should().BeFalse("方向が違う");
        ledger.WidenStopLoss(shortId, TradeSide.Sell, 102m).Should().BeTrue("空売りは上へ広げる");
        ledger.WidenStopLoss(shortId, TradeSide.Sell, 101.5m).Should().BeFalse();
        ledger.WidenStopLoss(unknownId, TradeSide.Buy, 90m).Should().BeFalse("不明のラインを埋めない");
        ledger.WidenStopLoss(closeId, TradeSide.Sell, 250m).Should().BeFalse("決済の承認は触らない");
        ledger.WidenStopLoss(Guid.NewGuid(), TradeSide.Buy, 1m).Should().BeFalse("承認が無い");

        var reread = kind == "ef" ? NewStore("ef", dbName) : ledger;
        reread.FindApprovedIntent(longId)!.StopLossPrice.Should().Be(226.2036m);
        reread.FindApprovedIntent(shortId)!.StopLossPrice.Should().Be(102m);
        reread.FindApprovedIntent(unknownId)!.StopLossPrice.Should().BeNull();
        reread.FindApprovedIntent(closeId)!.StopLossPrice.Should().Be(240m);
        reread.GetFills().Single(f => f.DecisionId == longId).StopLossPrice
            .Should().Be(226.2036m, "建玉の射影（市場監視が読むライン）が追随する");
    }

    // T-10-1929, FR-10, IADR-0472 決定6: ハンドラは事実のエントリー・方向・新ラインで台帳を書き換える。
    [Fact]
    public void T_10_1929_ハンドラは事実の新ラインで台帳を追随させる()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var entry = Guid.NewGuid();
        ledger.AppendApproval(entry, Open(TradeSide.Buy, 245.14m, "AMZN"), At);
        var handler = new SoftwareStopLineWidenedLedgerHandler(
            ledger, NullLogger<SoftwareStopLineWidenedLedgerHandler>.Instance);

        var message = new SoftwareStopLineWidened(
            entry, "AMZN", Market.UnitedStates, TradeSide.Buy, 248.01m, 245.14m, 243.0498m, 4.9602m,
            StopWidthFloorSource.Fallback2Pct, At);
        handler.Handle(message);
        handler.Handle(message); // 再配送

        ledger.FindApprovedIntent(entry)!.StopLossPrice.Should().Be(243.0498m);
    }

    // 🔴 T-10-1933, FR-10, IADR-0472 決定6: **本番の Program.cs の Wolverine 構成**が台帳の追随ハンドラを発見する
    // （規約発見から外れると、市場監視が旧ラインで到達を出し続ける）。
    [Fact]
    public async Task T_10_1933_本番構成で遡及の事実が台帳のラインへ届く()
    {
        await using var factory = new RiskWorkerWebApplicationFactory();
        var entry = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().AppendApproval(entry, Open(TradeSide.Buy, 503.98m, "MSFT"), At);

        await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new SoftwareStopLineWidened(
                entry, "MSFT", Market.UnitedStates, TradeSide.Buy, 511.91m, 503.98m, 501.6718m, 10.2382m,
                StopWidthFloorSource.Fallback2Pct, At));
        });

        using var check = factory.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<IPortfolioLedgerStore>().FindApprovedIntent(entry)!.StopLossPrice
            .Should().Be(501.6718m);
    }
}
