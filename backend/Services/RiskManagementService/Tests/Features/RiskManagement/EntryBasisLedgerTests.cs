using System.Text.Json;
using System.Text.Json.Nodes;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetOpenPositions;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-04, FR-10, #1384, IADR-0557 決定 4〜6: 新規建ての判断が建てた条件の記号と基準価格（OrderIntent.EntryBasis）を取引台帳の承認行へ永続化し、
// 射影のロットへ運び、GET /risk-controls/open-positions（REST・gRPC）でロットごとに出す。記録の無い行（列追加前・決済・取り込み）は null。
public class EntryBasisLedgerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 13, 30, 0, TimeSpan.Zero);

    public static TheoryData<string> Stores => new() { "ef", "memory" };

    private static IPortfolioLedgerStore NewStore(string kind, string dbName) => kind == "ef"
        ? new EfPortfolioLedgerStore(new RiskManagementDbContext(
            new DbContextOptionsBuilder<RiskManagementDbContext>().UseInMemoryDatabase(dbName).Options))
        : new InMemoryPortfolioLedgerStore();

    private static OrderIntent Intent(TradeSide side, int qty, PositionEffect effect, EntryBasis? basis) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper, qty, 96m, effect,
            effect == PositionEffect.Open ? 92m : null, EntryBasis: basis);

    // #1384 AC-10-01: 承認行に保存した記録が約定（射影の入力）へ届き、再読込でも残る。記録の無い承認は null のまま。
    [Theory]
    [MemberData(nameof(Stores))]
    public void 承認行の建てた条件と基準価格が約定へ届く(string kind)
    {
        var db = Guid.NewGuid().ToString();
        var ledger = NewStore(kind, db);
        var withBasis = Guid.NewGuid();
        var withoutBasis = Guid.NewGuid();
        var conditionOnly = Guid.NewGuid();
        ledger.AppendApproval(withBasis, Intent(TradeSide.Buy, 30, PositionEffect.Open, new EntryBasis("B", 95m)), T0);
        ledger.AppendFill(withBasis, "o-1", 30, 96m, T0);
        ledger.AppendApproval(withoutBasis, Intent(TradeSide.Buy, 20, PositionEffect.Open, null), T0.AddMinutes(1));
        ledger.AppendFill(withoutBasis, "o-2", 20, 96m, T0.AddMinutes(1));
        ledger.AppendApproval(conditionOnly, Intent(TradeSide.Buy, 10, PositionEffect.Open, new EntryBasis("A", null)), T0.AddMinutes(2));
        ledger.AppendFill(conditionOnly, "o-3", 10, 96m, T0.AddMinutes(2));

        var fills = (kind == "ef" ? NewStore("ef", db) : ledger).GetFills();

        fills.Single(f => f.DecisionId == withBasis).Should().Match<LedgerFill>(f => f.EntryCondition == "B" && f.EntryReferencePrice == 95m);
        fills.Single(f => f.DecisionId == withoutBasis).Should().Match<LedgerFill>(f => f.EntryCondition == null && f.EntryReferencePrice == null);
        fills.Single(f => f.DecisionId == conditionOnly).Should().Match<LedgerFill>(f => f.EntryCondition == "A" && f.EntryReferencePrice == null);
    }

    private static LedgerFill Fill(TradeSide side, int qty, int minute, Guid decisionId, string? condition, decimal? price,
        PositionEffect effect = PositionEffect.Open) =>
        new("AAPL", Market.UnitedStates, side, effect, qty, 96m, T0.AddMinutes(minute), effect == PositionEffect.Open ? 92m : null,
            DecisionId: decisionId, EntryOrderedAt: T0.AddMinutes(minute), EntryCondition: condition, EntryReferencePrice: price);

    // #1384 AC-10-02: 建て増しはロットを足し（古い順）、同じ判断の分割約定は 1 つに畳み、一部決済は古いロットから削り、全決済でロットは消える。
    [Fact]
    public void 射影のロットは建て増しで足し分割約定を畳み一部決済で古い順に削る()
    {
        var b = Guid.NewGuid();
        var legacy = Guid.Empty;
        var a = Guid.NewGuid();

        var lots = PortfolioProjection.ProjectOpenPositions(
        [
            Fill(TradeSide.Buy, 10, 0, legacy, null, null),
            Fill(TradeSide.Buy, 30, 5, b, "B", 95m),
            Fill(TradeSide.Buy, 20, 6, b, "B", 95m), // 同じ判断の分割約定
            Fill(TradeSide.Buy, 15, 10, a, "A", 100m),
            Fill(TradeSide.Sell, 25, 20, Guid.NewGuid(), null, null, PositionEffect.Close), // 古いロット（10 の不明 → B の 15）から削る
        ]).Should().ContainSingle().Subject.Lots;

        lots.Should().Equal(
            new OpenPositionLot(b, 35, T0.AddMinutes(5), "B", 95m),
            new OpenPositionLot(a, 15, T0.AddMinutes(10), "A", 100m));

        PortfolioProjection.ProjectOpenPositions(
        [
            Fill(TradeSide.Buy, 30, 5, b, "B", 95m),
            Fill(TradeSide.Sell, 30, 20, Guid.NewGuid(), null, null, PositionEffect.Close),
        ]).Should().BeEmpty("全決済でロットも消える");
    }

    // #1384 AC-10-03: REST のビューにロット（DecisionId・残数量・承認時刻・条件・基準価格）が出る。JSON は camelCase の entries。
    [Fact]
    public void 保有建玉のビューにロットごとの建てた条件が出る()
    {
        var ledger = new InMemoryPortfolioLedgerStore();
        var b = Guid.NewGuid();
        ledger.AppendApproval(b, Intent(TradeSide.Buy, 30, PositionEffect.Open, new EntryBasis("B", 95m)), T0);
        ledger.AppendFill(b, "o-1", 30, 96m, T0);

        var view = new OpenPositionsService(ledger).Build().Should().ContainSingle().Subject;

        view.Entries.Should().Equal(new OpenPositionEntryView(b, 30, T0, "B", 95m));
        var json = JsonSerializer.SerializeToNode(view, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        json["entries"]![0]!["entryCondition"]!.GetValue<string>().Should().Be("B");
        json["entries"]![0]!["entryReferencePrice"]!.GetValue<decimal>().Should().Be(95m);
        json["entries"]![0]!["decisionId"]!.GetValue<Guid>().Should().Be(b);
    }

    // #1384 AC-10-04: gRPC の行へ写す。null のビュー（供給なし）は entries を設定しない。記録の無いロットは条件・価格を設定しない。
    [Fact]
    public void gRPCの行へロットを写し供給なしは未設定にする()
    {
        var b = Guid.NewGuid();
        var view = new OpenPositionView("AAPL", Market.UnitedStates, TradeSide.Buy, 40, 96m, 92m,
            [new OpenPositionEntryView(b, 30, T0, "B", 95.5m), new OpenPositionEntryView(Guid.Empty, 10, T0, null, null)]);

        var row = RiskReadWireMapping.ToProto(view);

        row.Entries.Rows.Should().HaveCount(2);
        row.Entries.Rows[0].DecisionId.Should().Be(b.ToString());
        row.Entries.Rows[0].Quantity.Should().Be(30);
        row.Entries.Rows[0].OrderedAt.Should().Be(T0.ToString("O"));
        row.Entries.Rows[0].EntryCondition.Should().Be("B");
        row.Entries.Rows[0].EntryReferencePrice.Should().Be("95.5");
        row.Entries.Rows[1].HasEntryCondition.Should().BeFalse();
        row.Entries.Rows[1].HasEntryReferencePrice.Should().BeFalse();
        RiskReadWireMapping.ToProto(view with { Entries = null }).Entries.Should().BeNull();
    }
}
