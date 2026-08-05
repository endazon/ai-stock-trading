using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Infrastructure.Composable.StageGate;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.RiskManagement.Api.Tests;

// FR-20, FR-11, FR-09, UC-06, ADR-0008, IADR-0083, #166: 撤退の定期評価ドライバの結線を検証する。
// 休場ガード・新規停止時のみ通知（冪等）・fail-safe 非発火・多重実行防止（逐次）を受け入れ基準へ写像する。
// WebApplicationFactory（InMemory DB・Wolverine の外部トランスポート無効化）上で、ドライバを直接構成して
// RunOnceAsync を叩く。ADR-0013 / IADR-0129 / #354: 発行の観測は MassTransit の ITestHarness（ホスト寿命の
// 累積）から Wolverine.Tracking（追跡ブロック内の送信）へ移した。**複数回の巡回にまたがる件数を数えるテストは、
// 巡回すべてを 1 つの追跡ブロックに入れて累積の意味を保つ**。
public class WithdrawalEvaluationServiceTests
{
    // 2026-07-17 金曜（営業日）/ 2026-07-18 土曜（休場）。
    private static readonly DateTimeOffset FridayUtc = new(2026, 7, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaturdayUtc = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    // 段階を Stage 2（実弾最小）に据える。台帳は追記専用で現在段階は履歴の畳み込みなので、単一遷移で到達させる。
    private static void SeedStage(RiskWorkerWebApplicationFactory factory, TradingStage stage)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStageGateStore>().Append(new StageTransition(
            1, TradingStage.Stage0Verification, stage, StageTransitionKind.Promotion,
            "seed", FridayUtc, "test seed"));
    }

    private static void SeedPerformance(RiskWorkerWebApplicationFactory factory, StagePerformance performance)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStagePerformanceStore>().Save(performance);
    }

    // 実DD がバックテスト最大DD × 倍率を超える＝Stage 2/3 の撤退（自動停止）を発火させる実績。
    private static StagePerformance DrawdownBreach() => new()
    {
        BacktestMaxDrawdownRatio = 0.10m,
        ObservedMaxDrawdownRatio = 0.20m, // 0.10 × 1.5 = 0.15 を超過
    };

    // FR-20, #333, §4.3: Stage 1 の打ち切り（累計 120 営業日を経ても 100 件に届かない）＝非停止の降格提案
    // （HaltNewEntries=false）を発火させる実績。旧「ペーパー乖離が説明不能」は計画 §4.1 が機械判定から外した。
    private static StagePerformance Stage1Exhausted() => new()
    {
        Stage1QualifiedTradingDays = 120,
        Stage1TradeCount = 99,
    };

    // 件数に達した＝打ち切り非該当（提案は解消）。期間の要件は既に満たしている（§4.3）。
    private static StagePerformance Stage1Resolved() => new()
    {
        Stage1QualifiedTradingDays = 120,
        Stage1TradeCount = 100,
    };

    private static int NonHaltingCount(ITrackedSession session) =>
        session.Sent.MessagesOf<WithdrawalTriggered>().Count(m => !m.HaltNewEntries);

    private static WithdrawalEvaluationService BuildDriver(
        RiskWorkerWebApplicationFactory factory, DateTimeOffset now) =>
        new(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(now),
            new WeekendBusinessCalendar(),
            Options.Create(new WithdrawalEvaluationOptions()),
            NullLogger<WithdrawalEvaluationService>.Instance);

    private static bool KillSwitchEngaged(RiskWorkerWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<KillSwitchService>().GetState().Engaged;
    }

    [Fact]
    public async Task 撤退基準到達時に自動停止し_WithdrawalTriggered_を発行する()
    {
        // 受け入れ基準: 撤退基準到達時に kill switch 自動起動＋降格提案を通知（#15）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient(); // ホスト（＋ハーネスバス）を起動する。
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());

        var session = await factory.Services.ExecuteAndWaitAsync(
            () => BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None));

        KillSwitchEngaged(factory).Should().BeTrue();
        session.Sent.MessagesOf<WithdrawalTriggered>().Should().Contain(m =>
            m.HaltNewEntries
                && m.ProposedStage == (int)TradingStage.Stage0Verification
                && m.Reason == nameof(WithdrawalReason.DrawdownBreachedMultiple));
    }

    [Fact]
    public async Task 既に停止済みなら再発行しない_冪等()
    {
        // 受け入れ基準: 非発火時は副作用なし・冪等（既に kill switch 起動済みなら再起動しない＝再通知しない）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());
        var driver = BuildDriver(factory, FridayUtc);

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            await driver.RunOnceAsync(CancellationToken.None); // 1 回目: 新規停止 → 発行
            await driver.RunOnceAsync(CancellationToken.None); // 2 回目: 起動済み → 非発行
        });

        session.Sent.MessagesOf<WithdrawalTriggered>().Should().NotBeEmpty();
        session.Sent.MessagesOf<WithdrawalTriggered>()
            .Should().ContainSingle("新規に停止した 1 回だけ通知する（撤退継続中の再通知はしない）");
    }

    [Fact]
    public async Task 休場日は評価をスキップし発火しない()
    {
        // 受け入れ基準: 市場休場ガード。土曜は評価せず、撤退基準を満たす実績でも自動停止・通知しない。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());

        var session = await factory.Services.ExecuteAndWaitAsync(
            () => BuildDriver(factory, SaturdayUtc).RunOnceAsync(CancellationToken.None));

        KillSwitchEngaged(factory).Should().BeFalse();
        session.Sent.MessagesOf<WithdrawalTriggered>().Should().BeEmpty();
    }

    [Fact]
    public async Task 撤退基準に達していなければ副作用なし_非発行()
    {
        // 受け入れ基準: 実績未供給（既定・起点 Stage 0）は fail-safe で非発火＝停止も通知もしない。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();

        var session = await factory.Services.ExecuteAndWaitAsync(
            () => BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None));

        KillSwitchEngaged(factory).Should().BeFalse();
        session.Sent.MessagesOf<WithdrawalTriggered>().Should().BeEmpty();
    }

    // --- #189/IADR-0085: Stage 1 ペーパー乖離（非停止）の降格提案通知（durable な重複排除） ---

    [Fact]
    public async Task Stage1打ち切りで非停止の降格提案を通知する_停止しない()
    {
        // 受け入れ基準（#189・#333）: Stage 1 が累計 120 営業日で打ち切り → WithdrawalTriggered(HaltNewEntries=false,
        // ProposedStage=Stage0) を発行する。kill switch は起動しない（SIMULATE のため即時停止不要・降格提案に留める）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage1Simulate);
        SeedPerformance(factory, Stage1Exhausted());

        var session = await factory.Services.ExecuteAndWaitAsync(
            () => BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None));

        KillSwitchEngaged(factory).Should().BeFalse("Stage 1 の打ち切りは停止させず降格提案に留める");
        session.Sent.MessagesOf<WithdrawalTriggered>().Should().Contain(m =>
            !m.HaltNewEntries
                && m.ProposedStage == (int)TradingStage.Stage0Verification
                && m.Reason == nameof(WithdrawalReason.Stage1ExtensionExhausted));
    }

    [Fact]
    public async Task Stage1打ち切りが継続しても再通知しない_durable冪等()
    {
        // 受け入れ基準（#189）: 巡回ごとの重複通知を避ける。同一の打ち切りが継続する間は 1 回だけ通知する。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage1Simulate);
        SeedPerformance(factory, Stage1Exhausted());
        var driver = BuildDriver(factory, FridayUtc);

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            await driver.RunOnceAsync(CancellationToken.None); // 1 回目: 新規乖離 → 発行
            await driver.RunOnceAsync(CancellationToken.None); // 2 回目: 同一シグネチャ → 非発行
        });

        NonHaltingCount(session).Should().Be(1, "同一の打ち切り継続中は 1 回だけ通知する（スパム回避）");
    }

    [Fact]
    public async Task 別ドライバインスタンスでも重複排除する_再起動またぎ冪等()
    {
        // 受け入れ基準（#189）: 重複排除状態は DB 永続。別ドライバインスタンス（＝プロセス再起動の代理）でも再通知しない。
        // in-memory だと破綻する経路を、共有 DB のシグネチャで冪等化する（IADR-0085）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage1Simulate);
        SeedPerformance(factory, Stage1Exhausted());

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            await BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None); // インスタンス 1
            await BuildDriver(factory, FridayUtc).RunOnceAsync(CancellationToken.None); // インスタンス 2（別構築・同一 DB）
        });

        NonHaltingCount(session).Should().Be(1, "永続シグネチャで別インスタンス・再起動をまたいでも重複通知しない");
    }

    [Fact]
    public async Task 打ち切りが解消後に再発生したら再通知する()
    {
        // 受け入れ基準（#189）: 打ち切りが一旦解消（件数到達）してから再発生したら、再通知できる（シグネチャをクリアする）。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage1Simulate);
        var driver = BuildDriver(factory, FridayUtc);

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            SeedPerformance(factory, Stage1Exhausted());
            await driver.RunOnceAsync(CancellationToken.None); // 打ち切り → 発行（1）
            SeedPerformance(factory, Stage1Resolved());
            await driver.RunOnceAsync(CancellationToken.None); // 解消 → クリア・非発行
            SeedPerformance(factory, Stage1Exhausted());
            await driver.RunOnceAsync(CancellationToken.None); // 再打ち切り → 再発行（2）
        });

        NonHaltingCount(session).Should().Be(2, "解消を挟めば再度の打ち切りで再通知する");
    }

    [Fact]
    public async Task 停止経路は非停止の重複排除に影響されない_二重通知しない()
    {
        // 受け入れ基準（#189）: #166 停止経路（Stage 2/3）は従来どおり新規停止で 1 回のみ。非停止のシグネチャストアに
        // 依存せず、二重通知しない。停止経路は HaltNewEntries=true で発行される。
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.CreateClient();
        SeedStage(factory, TradingStage.Stage2MinimalLive);
        SeedPerformance(factory, DrawdownBreach());
        var driver = BuildDriver(factory, FridayUtc);

        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            await driver.RunOnceAsync(CancellationToken.None);
            await driver.RunOnceAsync(CancellationToken.None);
        });

        session.Sent.MessagesOf<WithdrawalTriggered>()
            .Should().ContainSingle("停止経路は新規停止の 1 回のみ")
            .Which.HaltNewEntries.Should().BeTrue();
        NonHaltingCount(session).Should().Be(0, "停止経路では非停止通知を出さない");
    }

    // 休場ガードの当日判定を制御するための固定時計。
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
