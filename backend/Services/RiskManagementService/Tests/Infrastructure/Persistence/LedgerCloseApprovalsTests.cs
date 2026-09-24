using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394 決定6 / T-10-777: 台帳の「決済の承認と約定時刻」の読み口（GetCloseApprovals）と、
// 承認行の由来（approved_orders.Source）の往復を、EF 実装と InMemory 実装の**同じシナリオ**で検査する
// （片方だけが正しいドリフトを防ぐ。WorkingEntryOrderSourceTests と同じ作法）。
public class LedgerCloseApprovalsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 13, 46, 45, TimeSpan.Zero);
    private static readonly DateTimeOffset Since = Now - StopOutProjection.Lookback;

    private static OrderIntent Intent(
        PositionEffect effect = PositionEffect.Close, string symbol = "AAPL", Market market = Market.UnitedStates,
        TradeSide side = TradeSide.Sell) =>
        new(symbol, market, side, ProductType.Cash, BrokerProvider.MoomooSimulate, 707, 337.455m, effect);

    private sealed record Ids(
        Guid S1Today, Guid S0OldFilledToday, Guid S0OldUnfilled, Guid LegacyToday, Guid OrderApprovedToday,
        Guid Entry, Guid OtherSymbol, Guid OtherMarket, Guid S0OldZeroFill, Guid ShortCover);

    private static Ids Seed(IPortfolioLedgerStore ledger)
    {
        var ids = new Ids(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var stopAt = new DateTimeOffset(2026, 9, 23, 13, 43, 33, TimeSpan.Zero);
        var armedLongAgo = Now.AddDays(-5);

        // S1 の発動（約定はまだ届いていない）。
        ledger.AppendApproval(ids.S1Today, Intent(), stopAt, source: ApprovalSource.SoftwareStopS1);
        // S0: 何日も前に武装し、当日に約定した（承認時刻は下限より前だが約定で拾う）。
        ledger.AppendApproval(ids.S0OldFilledToday, Intent(), armedLongAgo, source: ApprovalSource.ProtectiveStopS0);
        ledger.AppendFill(ids.S0OldFilledToday, "S0-FILLED", 707, 337.4m, stopAt);
        // S0: 何日も前に武装し、まだ約定していない（下限より前・当日の活動なし）→ 返さない。
        ledger.AppendApproval(ids.S0OldUnfilled, Intent(), armedLongAgo, source: ApprovalSource.ProtectiveStopS0);
        // S0: 約定数量 0 の行しか無い → 約定とは数えない（返さない）。
        ledger.AppendApproval(ids.S0OldZeroFill, Intent(), armedLongAgo, source: ApprovalSource.ProtectiveStopS0);
        ledger.AppendFill(ids.S0OldZeroFill, "S0-ZERO", 0, 0m, stopAt);
        // 由来が記録されていない決済（本列の追加前の行と同じ形＝source を渡さない）。
        ledger.AppendApproval(ids.LegacyToday, Intent(), stopAt);
        // OrderApproved 経由の決済（owner の手仕舞い等）。
        ledger.AppendApproval(ids.OrderApprovedToday, Intent(), stopAt, source: ApprovalSource.OrderApproved);
        // ショートの買戻し（決済の方向が買い）。
        ledger.AppendApproval(
            ids.ShortCover, Intent(side: TradeSide.Buy), stopAt, source: ApprovalSource.SoftwareStopS1);
        // 決済ではない・別銘柄・別市場 → 返さない。
        ledger.AppendApproval(ids.Entry, Intent(PositionEffect.Open, side: TradeSide.Buy), stopAt,
            source: ApprovalSource.OrderApproved);
        ledger.AppendApproval(ids.OtherSymbol, Intent(symbol: "MSFT"), stopAt, source: ApprovalSource.SoftwareStopS1);
        ledger.AppendApproval(ids.OtherMarket, Intent(market: Market.Japan), stopAt, source: ApprovalSource.SoftwareStopS1);
        return ids;
    }

    private static void AssertScenario(IPortfolioLedgerStore ledger, Ids ids)
    {
        var closes = ledger.GetCloseApprovals("AAPL", Market.UnitedStates, Since);

        closes.Select(c => c.DecisionId).Should().BeEquivalentTo(
            [ids.S1Today, ids.S0OldFilledToday, ids.LegacyToday, ids.OrderApprovedToday, ids.ShortCover]);

        var byId = closes.ToDictionary(c => c.DecisionId);
        byId[ids.S1Today].Source.Should().Be(ApprovalSource.SoftwareStopS1);
        byId[ids.S1Today].FillTimes.Should().BeEmpty();
        byId[ids.S0OldFilledToday].Source.Should().Be(ApprovalSource.ProtectiveStopS0);
        byId[ids.S0OldFilledToday].FillTimes.Should().ContainSingle()
            .Which.Should().Be(new DateTimeOffset(2026, 9, 23, 13, 43, 33, TimeSpan.Zero));
        // 🔴 由来を渡さなかった行は null のまま返る（OrderApproved などへ推定で埋めない）。
        byId[ids.LegacyToday].Source.Should().BeNull();
        byId[ids.OrderApprovedToday].Source.Should().Be(ApprovalSource.OrderApproved);
        byId[ids.ShortCover].Side.Should().Be(TradeSide.Buy);
        byId[ids.S1Today].Side.Should().Be(TradeSide.Sell);
    }

    [Fact]
    public void InMemory実装は決済の承認を由来と約定時刻つきで返す()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        AssertScenario(ledger, Seed(ledger));
    }

    [Fact]
    public void EF実装は決済の承認を由来と約定時刻つきで返す()
    {
        var dbName = Guid.NewGuid().ToString();
        Ids ids;
        using (var db = NewContext(dbName))
            ids = Seed(new EfPortfolioLedgerStore(db));

        // 別の DbContext で読む（変更追跡のキャッシュではなく保存された列を読む）。
        using var reader = NewContext(dbName);
        AssertScenario(new EfPortfolioLedgerStore(reader), ids);
        reader.ApprovedOrders.Single(a => a.DecisionId == ids.LegacyToday).Source.Should().BeNull();
        reader.ApprovedOrders.Single(a => a.DecisionId == ids.S1Today).Source.Should().Be(ApprovalSource.SoftwareStopS1);
    }

    // 承認は冪等（同じ DecisionId の再送は無視）。再送が由来を運んでも、最初の行の由来は書き換わらない。
    [Fact]
    public void 由来は最初の承認で固定され再送で書き換わらない()
    {
        var id = Guid.NewGuid();
        var inMemory = new InMemoryPortfolioLedgerStore();
        using var db = NewContext(Guid.NewGuid().ToString());
        var ef = new EfPortfolioLedgerStore(db);

        foreach (var ledger in new IPortfolioLedgerStore[] { inMemory, ef })
        {
            ledger.AppendApproval(id, Intent(), Now, source: ApprovalSource.SoftwareStopS1);
            ledger.AppendApproval(id, Intent(), Now, source: ApprovalSource.OrderApproved);

            ledger.GetCloseApprovals("AAPL", Market.UnitedStates, Since)
                .Should().ContainSingle().Which.Source.Should().Be(ApprovalSource.SoftwareStopS1);
        }
    }

    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);
}
