using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Domain;
using RiskManagementService.Hosted;
using RiskManagementService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-11, UC-06, ADR-0003, ADR-0009, ADR-0016 決定7, #330, #634, IADR-0133, IADR-0298:
// 維持率割れ自動縮小の定期評価ドライバ（MaintenanceMarginEvaluationService）の結線を検証する。
//
// 受け入れ基準（#634）:
//   (a) 供給される構成で閾値割れなら MaintenanceMarginReductionExecuted が publish される
//   (b) 否定形（最重要）: 供給元が Unavailable* を返す間は縮小注文も通知も一切発生しない
//   (c) 否定形: kill switch / 日次損失ロックアウト / 一時停止が成立していても自動縮小は動く
//
// 実 DI（RiskWorkerWebApplicationFactory）の駆動自体は既定有効（#634・IADR-0298）だが、本クラスは
// RunOnceAsync を直接叩く単体観点のため、各テストで実駆動（BackgroundService の自動巡回）を明示的に
// 無効化し、二重発行を避ける（駆動の存在そのものは MaintenanceMarginEvaluationWiringTests が別途固定する）。
public class MaintenanceMarginEvaluationServiceTests
{
    // 2026-07-17 金曜（営業日）/ 2026-07-18 土曜（休場）。WithdrawalEvaluationServiceTests と同じ基準日。
    private static readonly DateTimeOffset FridayUtc = new(2026, 7, 17, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SaturdayUtc = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedSnapshotSource(MaintenanceMarginSnapshot? snapshot) : IMaintenanceMarginSnapshotSource
    {
        public MaintenanceMarginSnapshot? GetCurrent() => snapshot;
    }

    private static MarginPosition Short(string symbol, decimal price, int quantity, decimal requiredMargin) =>
        new()
        {
            Symbol = symbol,
            Market = Market.UnitedStates,
            Side = TradeSide.Sell,
            ProductType = ProductType.ShortSell,
            Quantity = quantity,
            PriceUsd = price,
            RequiredMarginUsd = requiredMargin,
        };

    // 純資産 40,000／建玉 100,000（$100 × 1,000 株）＝維持率 40%（閾値ちょうど・発動する）。
    // MaintenanceMarginReductionServiceTests.BreachedSnapshot と同一入力（回帰の基準を揃える）。
    private static MaintenanceMarginSnapshot BreachedSnapshot() =>
        new() { NetEquityUsd = 40_000m, Positions = [Short("AAPL", 100m, 1_000, 30_000m)] };

    private static WebApplicationFactory<Program> WireWith(
        RiskWorkerWebApplicationFactory factory, IMaintenanceMarginSnapshotSource source) =>
        factory.WithWebHostBuilder(b =>
        {
            // 実駆動（BackgroundService）は既定有効のため、RunOnceAsync を直接叩く本テストでは無効化する。
            b.UseSetting("MaintenanceMarginEvaluation:Enabled", "false");
            b.ConfigureServices(services => services.AddSingleton(source));
        });

    private static MaintenanceMarginEvaluationService BuildDriver(
        IServiceProvider services, DateTimeOffset now) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(now),
            new WeekendBusinessCalendar(),
            Options.Create(new MaintenanceMarginEvaluationOptions()),
            NullLogger<MaintenanceMarginEvaluationService>.Instance);

    // T-10-322（a）: 供給される構成で閾値割れなら決済承認と記録イベントが発行される。
    [Fact]
    public async Task 閾値割れの構成では決済承認と記録イベントが発行される()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = WireWith(factory, new FixedSnapshotSource(BreachedSnapshot()));
        wired.CreateClient(); // ホスト（＋ハーネスバス）を起動する。

        var session = await wired.Services.ExecuteAndWaitAsync(
            () => BuildDriver(wired.Services, FridayUtc).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<OrderApproved>().Should().ContainSingle()
            .Which.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        session.Sent.MessagesOf<MaintenanceMarginReductionExecuted>().Should().ContainSingle();
    }

    // T-10-323（b・否定形・最重要）: 供給元が Unavailable（null）を返す間は縮小注文も通知も一切発生しない。
    [Fact]
    public async Task 供給元が供給なしを返す間は決済も記録イベントも発生しない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = WireWith(factory, new UnavailableMaintenanceMarginSnapshotSource());
        wired.CreateClient();

        var session = await wired.Services.ExecuteAndWaitAsync(
            () => BuildDriver(wired.Services, FridayUtc).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty();
        session.Sent.MessagesOf<MaintenanceMarginReductionExecuted>().Should().BeEmpty();
    }

    // T-10-324（b・否定形の別角度）: SnapshotUntrusted（壊れたスナップショット）でも決済も記録イベントも発生しない
    // （警告ログは MaintenanceMarginReductionService 側の既存責務であり、ドライバは publish しない）。
    [Fact]
    public async Task スナップショットが信頼できない間は決済も記録イベントも発生しない()
    {
        var untrusted = new MaintenanceMarginSnapshot
        {
            NetEquityUsd = 40_000m,
            Positions = [Short("BROKEN", 0m, 1_000, 30_000m)],
        };
        using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = WireWith(factory, new FixedSnapshotSource(untrusted));
        wired.CreateClient();

        var session = await wired.Services.ExecuteAndWaitAsync(
            () => BuildDriver(wired.Services, FridayUtc).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty();
        session.Sent.MessagesOf<MaintenanceMarginReductionExecuted>().Should().BeEmpty();
    }

    // T-10-325: 休場ガード（他 2 件の定期評価ドライバと同型）。土曜は評価しない。
    [Fact]
    public async Task 休場日は評価をスキップし発行しない()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = WireWith(factory, new FixedSnapshotSource(BreachedSnapshot()));
        wired.CreateClient();

        var session = await wired.Services.ExecuteAndWaitAsync(
            () => BuildDriver(wired.Services, SaturdayUtc).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<OrderApproved>().Should().BeEmpty();
        session.Sent.MessagesOf<MaintenanceMarginReductionExecuted>().Should().BeEmpty();
    }

    // T-10-326（c・否定形）: kill switch / 日次損失ロックアウト / 一時停止が成立していても自動縮小は動く
    // （FR-10「いずれも手仕舞い（Close）と損切りは止めない」。ADR-0009 の構造的保証を実 DI 経由で固定する）。
    [Fact]
    public async Task 三統制が成立していても自動縮小は動く()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        using var wired = WireWith(factory, new FixedSnapshotSource(BreachedSnapshot()));
        wired.CreateClient();

        using (var scope = wired.Services.CreateScope())
        {
            // kill switch を起動する。
            scope.ServiceProvider.GetRequiredService<KillSwitchService>().Engage("test", "T-10-326 テスト");
            // 一時停止も併せて起動する（統制の重ね掛けでも動くことを固定する）。
            scope.ServiceProvider.GetRequiredService<PauseService>().Pause("test", "T-10-326 テスト");
        }

        var session = await wired.Services.ExecuteAndWaitAsync(
            () => BuildDriver(wired.Services, FridayUtc).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<OrderApproved>().Should().ContainSingle(
            "3 統制が成立していても自動縮小は止まらない（FR-10・ADR-0009）");
        session.Sent.MessagesOf<MaintenanceMarginReductionExecuted>().Should().ContainSingle();
    }

    // 休場ガードの当日判定を制御するための固定時計（WithdrawalEvaluationServiceTests と同型）。
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;

        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);
    }
}
