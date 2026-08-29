using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153:
// 口座種別の観測（BrokerAccountObserved）が判定コアへ届く**供給経路**の検証。
//
// 統制そのものは RiskEvaluator（純関数・CashAccountControlsTests）で固定してある。ここで確かめるのは
// 「観測が届くこと」と「**届かなければ未確定のままであること**」——供給が無いのに既定値が入る経路があると、
// 判定側の fail-closed が丸ごと無効になる。
public class BrokerAccountObservedConsumerTests
{
    private static readonly DateTimeOffset Observed = new(2026, 8, 6, 13, 30, 0, TimeSpan.Zero);

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Task<IHost> BuildHostAsync(IBrokerAccountObservationStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(store);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<BrokerAccountObservedHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    [Fact]
    public async Task 観測された口座種別がストアへ記録される()
    {
        var store = new InMemoryBrokerAccountObservationStore(new StubTimeProvider(Observed));
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new BrokerAccountObserved(
            BrokerProvider.MoomooSimulate, new BrokerAccountState(AccountType.Cash, 1_000m), Observed));

        store.GetCurrent().Should().Be(new BrokerAccountState(AccountType.Cash, 1_000m));
    }

    // **否定形**: イベントが 1 件も届かなければ未確定のままである（既定値へ落ちない）。
    [Fact]
    public async Task 観測が届かなければ口座種別は未確定のままである()
    {
        var store = new InMemoryBrokerAccountObservationStore(new StubTimeProvider(Observed));
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => Task.CompletedTask);

        store.GetCurrent().Should().BeNull();
    }

    // ADR-0013, IADR-0129 決定10: 再配送で同じ観測が 2 度届いても状態が動かない（冪等）。
    [Fact]
    public async Task 同一観測の再配送は状態を変えない()
    {
        var store = new InMemoryBrokerAccountObservationStore(new StubTimeProvider(Observed));
        using var host = await BuildHostAsync(store);
        var message = new BrokerAccountObserved(
            BrokerProvider.MoomooSimulate, new BrokerAccountState(AccountType.Margin), Observed);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(message);
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(message);

        store.GetCurrent()!.AccountType.Should().Be(AccountType.Margin);
    }

    // 順序が入れ替わって届いても、**古い種別が新しい観測を上書きしない**。
    [Fact]
    public async Task 順序が入れ替わって届いても新しい観測が保たれる()
    {
        var store = new InMemoryBrokerAccountObservationStore(new StubTimeProvider(Observed));
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new BrokerAccountObserved(
            BrokerProvider.MoomooSimulate, new BrokerAccountState(AccountType.Cash, 1m), Observed));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(new BrokerAccountObserved(
            BrokerProvider.MoomooSimulate, new BrokerAccountState(AccountType.Margin), Observed.AddMinutes(-10)));

        store.GetCurrent()!.AccountType.Should().Be(AccountType.Cash);
    }

    // #258 の再発経路（competing consumer）を構造的に閉じる。他の購読とキュー名が衝突しないこと。
    [Fact]
    public void 口座種別購読のキュー名は他の購読と衝突しない()
    {
        const string serviceName = "ai-stock-trading.risk-management-service";

        var accountQueue = AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions.WolverineExtensions
            .QueueNameFor(serviceName, typeof(BrokerAccountObserved));

        accountQueue.Should().Be("ai-stock-trading.risk-management-service.BrokerAccountObserved");
        accountQueue.Should().NotBe(AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions.WolverineExtensions
            .QueueNameFor(serviceName, typeof(BrokerAvailabilityObserved)));
    }
}
