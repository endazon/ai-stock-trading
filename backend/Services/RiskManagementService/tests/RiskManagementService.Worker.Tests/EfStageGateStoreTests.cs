using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Worker.Foundation.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Worker.Tests;

// FR-20, UC-06, IADR-0070: 段階ゲートの EF 永続化を InMemory DB で検証する（追記専用台帳・単一行実績）。
public class EfStageGateStoreTests
{
    private static RiskManagementDbContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options);

    [Fact]
    public void 空の台帳は_Stage0_起点を返す()
    {
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfStageGateStore(db);

        var ledger = store.Load();

        ledger.CurrentStage.Should().Be(TradingStage.Stage0Verification);
        ledger.History.Should().BeEmpty();
        ledger.NextSequence.Should().Be(1);
    }

    [Fact]
    public void 遷移は追記され現在段階が畳み込みで更新され別コンテキストからも読める()
    {
        // 受け入れ基準: 遷移履歴が監査できる（永続化・別スコープで読める）。
        var dbName = Guid.NewGuid().ToString();
        var transition = new StageTransition(
            1, TradingStage.Stage0Verification, TradingStage.Stage1Paper,
            StageTransitionKind.Promotion, "owner", DateTimeOffset.UtcNow, "利用者承認による昇格");

        using (var db = NewContext(dbName))
        {
            new EfStageGateStore(db).Append(transition);
        }

        using var db2 = NewContext(dbName);
        var ledger = new EfStageGateStore(db2).Load();
        ledger.History.Should().HaveCount(1);
        ledger.CurrentStage.Should().Be(TradingStage.Stage1Paper);
        ledger.NextSequence.Should().Be(2);
        ledger.History[0].ApprovedBy.Should().Be("owner");
    }

    // 注（IADR-0070 決定1）: 同一 Sequence の並行二重追記は relational プロバイダで一意制約違反（DbUpdateException）となり、
    // EfStageGateStore.Append がこれを DbUpdateConcurrencyException へ変換して 409 に写像する。InMemory プロバイダは
    // 一意制約を relational と同型にモデル化せず（キー重複を ArgumentException として送出する）この 409 経路を再現できないため、
    // 本経路のテストは実 Postgres 前提の実コンテナ E2E（#82 系）に切り分ける（500 は両プロバイダで回避される）。

    [Fact]
    public void 段階別実績は未記録時に_fail_safe既定_を返す()
    {
        // fail-safe: 未記録は BacktestPassed=false＝昇格を許可しない安全側。
        var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfStagePerformanceStore(db);

        var performance = store.GetCurrent();

        performance.BacktestPassed.Should().BeFalse();
        performance.ObservedMaxDrawdownRatio.Should().Be(0m);
    }

    [Fact]
    public void 段階別実績は_upsert_され別コンテキストからも読める()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var db = NewContext(dbName))
        {
            new EfStagePerformanceStore(db).Save(new StagePerformance
            {
                BacktestPassed = true,
                BacktestMaxDrawdownRatio = 0.10m,
            });
        }
        using (var db2 = NewContext(dbName))
        {
            // 更新（upsert）で単一行を書き換える。
            new EfStagePerformanceStore(db2).Save(new StagePerformance
            {
                BacktestPassed = true,
                BacktestMaxDrawdownRatio = 0.12m,
                ObservedMaxDrawdownRatio = 0.05m,
            });
        }

        using var db3 = NewContext(dbName);
        var reloaded = new EfStagePerformanceStore(db3).GetCurrent();
        reloaded.BacktestPassed.Should().BeTrue();
        reloaded.BacktestMaxDrawdownRatio.Should().Be(0.12m);
        reloaded.ObservedMaxDrawdownRatio.Should().Be(0.05m);
    }
}
