using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Domain;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Tests;

// FR-20, FR-11, #387, 06_daytrading-review §4.1 条件1, IADR-0148:
// 発注審査の観測ログ（統制違反件数の供給元）の EF ストアを InMemory DB で検証する。
// 集計規則そのものは ControlViolationAggregationTests が権威。ここは
// **永続化した場合も同じ結果になること**と**冪等・窓の区切り**を確かめる。
public class EfControlViolationObservationStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static OrderScreeningObservation Simulate(Guid id, params RejectionReason[] reasons) =>
        new(id, BrokerProvider.MoomooSimulate, reasons);

    // **本 issue の核心**: 記録が無ければ **未供給（null）** を返す。0 件を返さない。
    [Fact]
    public void 記録が無ければ未供給を返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IControlViolationObservationStore store = new EfControlViolationObservationStore(db);

        store.GetTally().Should().BeNull();
    }

    // 正の確認: クラス C の拒否は 1 件として集計され、別スコープからも同じ結果が読める。
    [Fact]
    public void クラスCの拒否を1件として集計し別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            IControlViolationObservationStore store = new EfControlViolationObservationStore(db);
            store.Record(Simulate(Guid.NewGuid(), RejectionReason.BannedSymbol));
            store.Record(Simulate(Guid.NewGuid()));
        }

        using var db2 = NewContext(dbName);
        var tally = new EfControlViolationObservationStore(db2).GetTally();

        tally.Should().NotBeNull();
        tally!.Count.Should().Be(1);
    }

    // **否定形**: 同一 DecisionId の再記録（メッセージ再送）で二重計上しない。
    // 計上単位「1 回の発注拒否につき 1 件」は主キー制約が担保する。
    [Fact]
    public void 同一DecisionIdの再記録は二重計上しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IControlViolationObservationStore store = new EfControlViolationObservationStore(db);
        var decisionId = Guid.NewGuid();

        store.Record(Simulate(decisionId, RejectionReason.BannedSymbol));
        store.Record(Simulate(decisionId, RejectionReason.BannedSymbol));
        store.Record(Simulate(decisionId, RejectionReason.ManipulativeOrderPattern));

        store.GetTally()!.Count.Should().Be(1);
    }

    // **否定形**: 1 回の拒否に複数のクラス C 理由が含まれても 1 件である。
    [Fact]
    public void 拒否1回に複数のクラスC理由が含まれても1件である()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IControlViolationObservationStore store = new EfControlViolationObservationStore(db);

        store.Record(Simulate(
            Guid.NewGuid(),
            RejectionReason.BannedSymbol,
            RejectionReason.ManipulativeOrderPattern));

        store.GetTally()!.Count.Should().Be(1);
    }

    // **否定形**（§4.1・ADR-0016 決定10）: クラス A / B は件数を増やさない（供給はされる）。
    [Fact]
    public void クラスAとクラスBの拒否は件数を増やさない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IControlViolationObservationStore store = new EfControlViolationObservationStore(db);

        store.Record(Simulate(Guid.NewGuid(), RejectionReason.ShortSellDisabled));
        store.Record(Simulate(Guid.NewGuid(), RejectionReason.KillSwitchActive));
        store.Record(Simulate(Guid.NewGuid(), RejectionReason.StageProductTypeProhibited));

        var tally = store.GetTally();
        tally.Should().NotBeNull("審査は動いている＝集計は供給されている");
        tally!.Count.Should().Be(0);
    }

    // **否定形**（FR-20・IADR-0142 決定2）: 内蔵 paper の審査は供給も件数も作らない。
    [Fact]
    public void 内蔵paperの審査は供給も件数も作らない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IControlViolationObservationStore store = new EfControlViolationObservationStore(db);

        store.Record(new OrderScreeningObservation(
            Guid.NewGuid(), BrokerProvider.InternalPaper, [RejectionReason.BannedSymbol]));

        store.GetTally().Should().BeNull();
    }

    // FR-20, §4.1「集計期間は Stage 1 の全期間」: 窓を区切ると未供給へ戻る（fail-safe）。
    [Fact]
    public void 窓を区切ると未供給へ戻る()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IControlViolationObservationStore store = new EfControlViolationObservationStore(db);
        store.Record(Simulate(Guid.NewGuid(), RejectionReason.BannedSymbol));

        store.ResetWindow();

        store.GetTally().Should().BeNull();

        // 区切ったあとに同じ DecisionId が来ても新しい窓では改めて数えられる（冪等鍵は窓を跨がない）。
        var decisionId = Guid.NewGuid();
        store.Record(Simulate(decisionId, RejectionReason.ManipulativeOrderPattern));
        store.GetTally()!.Count.Should().Be(1);
    }
}
