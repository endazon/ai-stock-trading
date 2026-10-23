using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Hosted;
using RiskManagementService.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, FR-09, SC-03, #1423 M-3, ADR-0073 決定3, IADR-0581 決定3:
// **基準資金の口座照会の欠けを、取引日（米国東部の暦日）ごとに 1 回だけ知らせる。**
//
// 計画は「欠けを検知したことを Discord へ知らせる（リスク統制の発動・緊急度 高）」「拒否のたびに鳴らして本物の警告を
// 埋もれさせない」と定める。発行は本番と同じ Wolverine の実行時（試験 factory の外部トランスポートは無効化）から作る
// MessageBus で行い、Wolverine.Tracking で数える。複数回の巡回にまたがる件数は 1 つの追跡ブロックで数える。
public class CapitalBaselineObservationMissingServiceTests
{
    private static readonly DateOnly Monday = new(2026, 7, 13);
    private static readonly DateOnly Friday = new(2026, 7, 10);

    private sealed class SettableStore : ICapitalBaselineStore
    {
        public CapitalBaselineReading Reading { get; set; } =
            new(null, CapitalBaselineReadOutcome.UnavailableNoRow, Monday, null, null);

        public void Record(decimal equityInBase, DateTimeOffset observedAt)
        {
        }

        public CapitalBaseline? GetCurrent() => Reading.Baseline;

        public CapitalBaselineReading Read() => Reading;
    }

    private static CapitalBaselineReading Missing(DateOnly today, CapitalBaselineReadOutcome outcome =
        CapitalBaselineReadOutcome.UnavailableMissingPreviousDay) => new(null, outcome, today, Friday, 3_000m);

    // 本番の常駐（実時計・実 DB）は止め、試験が組み立てた巡回の発行だけを数える（同じ実行時の発行が混ざらないように）。
    private static RiskWorkerWebApplicationFactory NewFactory() => new()
    {
        HostSettings = { ["Risk:CapitalBaseline:NotifyObservationMissing"] = "false" },
    };

    private static (CapitalBaselineObservationMissingService Service, SettableStore Store, Func<bool> BusAvailable, Action<bool> SetBus)
        Build(RiskWorkerWebApplicationFactory factory)
    {
        var store = new SettableStore();
        var busAvailable = true;
        var runtime = factory.Services.GetRequiredService<IWolverineRuntime>();
        var services = new ServiceCollection();
        services.AddSingleton<ICapitalBaselineStore>(store);
        services.AddScoped<IMessageBus>(_ => busAvailable
            ? new MessageBus(runtime)
            : throw new InvalidOperationException("発行の失敗を模す"));
        var provider = services.BuildServiceProvider();
        var service = new CapitalBaselineObservationMissingService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(new DateTimeOffset(2026, 7, 13, 13, 0, 0, TimeSpan.Zero), Monday),
            new CapitalBaselineOptions(),
            NullLogger<CapitalBaselineObservationMissingService>.Instance);
        return (service, store, () => busAvailable, v => busAvailable = v);
    }

    // 🔴 T-10-3040: 欠けを検知したら 1 回発行し、**同じ取引日の 2 回目以降は発行しない**。翌取引日も欠けていれば再び 1 回発行する。
    [Fact]
    public async Task 欠けは取引日ごとに1回だけ発行する()
    {
        using var factory = NewFactory();
        factory.CreateClient();
        var (service, store, _, _) = Build(factory);
        store.Reading = Missing(Monday);

        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            (await service.RunOnceAsync(CancellationToken.None)).Should().BeTrue();
            (await service.RunOnceAsync(CancellationToken.None)).Should().BeFalse("同じ取引日は 1 回だけ");
            store.Reading = Missing(Monday.AddDays(1), CapitalBaselineReadOutcome.UnavailableStale);
            (await service.RunOnceAsync(CancellationToken.None)).Should().BeTrue("翌取引日は改めて知らせる");
        });

        var sent = session.Sent.MessagesOf<CapitalBaselineObservationMissing>().ToList();
        sent.Should().HaveCount(2);
        sent[0].Should().BeEquivalentTo(new { TradingDay = Monday, LatestObservedTradingDay = Friday, Stale = false });
        sent[1].Stale.Should().BeTrue("鮮度切れ（巡回の長い停止）も欠けとして知らせ、区別を載せる");
    }

    // T-10-3041（否定形）: 供給できている・行が無い（初回の配備）・人手の 0（意図した停止）では発行しない。
    [Theory]
    [InlineData(CapitalBaselineReadOutcome.Supplied)]
    [InlineData(CapitalBaselineReadOutcome.UnavailableNoRow)]
    [InlineData(CapitalBaselineReadOutcome.UnavailableNonPositive)]
    public async Task 欠けでない帰結では発行しない(CapitalBaselineReadOutcome outcome)
    {
        using var factory = NewFactory();
        factory.CreateClient();
        var (service, store, _, _) = Build(factory);
        store.Reading = new CapitalBaselineReading(null, outcome, Monday, Friday, 3_000m);

        var session = await factory.Services.ExecuteAndWaitForTestAsync(
            () => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<CapitalBaselineObservationMissing>().Should().BeEmpty();
    }

    // T-10-3042: 発行に失敗したら「通知済み」を戻し、**次の巡回で再試行する**（失敗を恒久的に握り潰さない）。
    [Fact]
    public async Task 発行に失敗したら次の巡回で再試行する()
    {
        using var factory = NewFactory();
        factory.CreateClient();
        var (service, store, _, setBus) = Build(factory);
        store.Reading = Missing(Monday);

        setBus(false);
        await FluentActions.Awaiting(() => service.RunOnceAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        setBus(true);

        var session = await factory.Services.ExecuteAndWaitForTestAsync(
            () => service.RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<CapitalBaselineObservationMissing>().Should().ContainSingle();
    }

    // T-10-3043（結線）: 本番の Program.cs は既定で巡回を常駐させる（計画が通知を求める統制。opt-out のみ）。
    [Fact]
    public void Programは既定で欠けの巡回を常駐させ_明示の無効化でだけ外す()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        factory.Services.GetServices<IHostedService>()
            .Should().Contain(s => s is CapitalBaselineObservationMissingService);

        using var disabled = NewFactory();
        disabled.Services.GetServices<IHostedService>()
            .Should().NotContain(s => s is CapitalBaselineObservationMissingService);
    }
}
