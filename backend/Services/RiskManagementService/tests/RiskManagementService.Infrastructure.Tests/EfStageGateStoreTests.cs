using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

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

    [Fact]
    public void 別供給源の同時_read_modify_write_で担当外フィールドを失わない()
    {
        // IADR-0089/0103 決定7: 単一行 StagePerformance には複数の供給源（backtest 由来の射影 / 実DD ドライバ /
        // 差し戻しリセット）が read-modify-write で書き込む。各供給源は担当外フィールドを読み値のまま書き戻すため、
        // EF Core の変更追跡が「実際に値が変わったプロパティ」だけを UPDATE に含める＝担当外の列を送信しない。
        // よって供給源が互いの列を巻き戻すロスト・アップデートは構造的に起きない。本テストはその契約を固定する。
        //
        // 筋書き: A が T0 に読み（backtest 未合格）、その後 B が backtest verdict を確定させ、最後に A が
        // 自分の担当（実DD）だけを更新して保存する。A の古い backtest 値で B の確定が巻き戻ってはならない。
        var dbName = Guid.NewGuid().ToString();
        using (var seed = NewContext(dbName))
            new EfStagePerformanceStore(seed).Save(new StagePerformance());

        using var dbA = NewContext(dbName);
        var storeA = new EfStagePerformanceStore(dbA);
        var snapshotA = storeA.GetCurrent(); // T0: BacktestPassed=false
        snapshotA.BacktestPassed.Should().BeFalse();

        using (var dbB = NewContext(dbName))
        {
            // T1: 別供給源（BacktestEvaluatedProjectionConsumer 相当）が verdict を確定させる。
            new EfStagePerformanceStore(dbB).Save(
                new StagePerformance { BacktestPassed = true, BacktestMaxDrawdownRatio = 0.10m });
        }

        // T2: A が古いスナップショットを基に自分の担当（実DD）だけを更新する。
        storeA.Save(snapshotA with { ObservedMaxDrawdownRatio = 0.20m });

        using var db3 = NewContext(dbName);
        var reloaded = new EfStagePerformanceStore(db3).GetCurrent();
        reloaded.ObservedMaxDrawdownRatio.Should().Be(0.20m, "実DD 供給源の更新は反映される");
        reloaded.BacktestPassed.Should().BeTrue("担当外フィールドを古い値で巻き戻さない");
        reloaded.BacktestMaxDrawdownRatio.Should().Be(0.10m, "担当外フィールドを古い値で巻き戻さない");
    }
}
