using RiskManagementService.Application.Ports;
using RiskManagementService.Domain;
using RiskManagementService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RiskManagementService.Infrastructure.Tests;

// FR-20, FR-12, #386, 06_daytrading-review §4.1 条件3, IADR-0149:
// 約定の観測ログ（Stage 1 の取引件数の供給元）の EF ストアを InMemory DB で検証する。
// 集計規則そのものは Stage1TradeCountUnitTests が権威。ここは
// **永続化した場合も同じ結果になること**と**冪等（計上単位）・窓の区切り**を確かめる。
public class EfStage1FillObservationStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    private static Stage1FillObservation Fill(
        Guid id,
        BrokerProvider provider = BrokerProvider.MoomooSimulate,
        PositionEffect effect = PositionEffect.Open) =>
        new(id, new DateOnly(2026, 8, 5), provider, effect);

    // 記録が無ければ 0 件。**0 は fail-safe**（条件未充足＝昇格しない）であり、
    // 統制違反件数（#387）のように「未供給」を別状態で持つ必要が無い。
    [Fact]
    public void 記録が無ければ0件を返す()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1FillObservationStore store = new EfStage1FillObservationStore(db);

        store.GetTradeCount().Should().Be(0);
    }

    // 正の確認: SIMULATE の新規建て約定が数えられ、別スコープからも同じ結果が読める。
    [Fact]
    public void SIMULATEの新規建て約定を数え別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            IStage1FillObservationStore store = new EfStage1FillObservationStore(db);
            store.Record(Fill(Guid.NewGuid()));
            store.Record(Fill(Guid.NewGuid()));
        }

        using var db2 = NewContext(dbName);
        new EfStage1FillObservationStore(db2).GetTradeCount().Should().Be(2);
    }

    // **否定形**: 同一 DecisionId の再記録（分割約定の続報・メッセージ再送）で二重計上しない。
    // 計上単位「約定した注文 1 件」は主キー制約が担保する。
    [Fact]
    public void 同一DecisionIdの再記録は二重計上しない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1FillObservationStore store = new EfStage1FillObservationStore(db);
        var decisionId = Guid.NewGuid();

        store.Record(Fill(decisionId));
        store.Record(Fill(decisionId));
        store.Record(Fill(decisionId));

        store.GetTradeCount().Should().Be(1);
    }

    // **否定形（本 issue の核心）**: 内蔵 paper の約定は永続化されても 1 件も数えられない。
    [Fact]
    public void 内蔵paperの約定は永続化しても件数を作らない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1FillObservationStore store = new EfStage1FillObservationStore(db);

        for (var i = 0; i < 10; i++)
        {
            store.Record(Fill(Guid.NewGuid(), BrokerProvider.InternalPaper));
        }

        store.GetTradeCount().Should().Be(0);
    }

    // **否定形**: 実弾（許可されていない発注先）・手仕舞いも数えない。
    [Fact]
    public void 実弾と手仕舞いは件数を作らない()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1FillObservationStore store = new EfStage1FillObservationStore(db);

        store.Record(Fill(Guid.NewGuid(), BrokerProvider.MoomooReal));
        store.Record(Fill(Guid.NewGuid(), effect: PositionEffect.Close));

        store.GetTradeCount().Should().Be(0);
    }

    // FR-20, §4.2「起算点＝Stage 1 遷移日」: 窓を区切ると 0 件へ戻る（fail-safe）。
    [Fact]
    public void 窓を区切ると0件へ戻る()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        IStage1FillObservationStore store = new EfStage1FillObservationStore(db);
        store.Record(Fill(Guid.NewGuid()));

        store.ResetWindow();

        store.GetTradeCount().Should().Be(0);

        // 区切ったあとに同じ DecisionId が来ても新しい窓では改めて数えられる（冪等鍵は窓を跨がない）。
        var decisionId = Guid.NewGuid();
        store.Record(Fill(decisionId));
        store.GetTradeCount().Should().Be(1);
    }
}
