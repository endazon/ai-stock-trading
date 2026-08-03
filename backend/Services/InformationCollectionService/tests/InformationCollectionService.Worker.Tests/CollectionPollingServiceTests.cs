using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Polling;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using AppSvc = AiStockTrading.InformationCollection.Application.Services.InformationCollectionService;

namespace AiStockTrading.InformationCollection.Worker.Tests;

// FR-01, FR-02: 1 巡回で収集→保存し、収集があれば InformationCollected を発行することを検証する。
public class CollectionPollingServiceTests
{
    private sealed class StubSource(IReadOnlyList<RawInformationItem> items) : IInformationSource
    {
        public Task<IReadOnlyList<RawInformationItem>> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(items);
    }

    // 費用統制ゲートのフェイク（既定 Normal・Halted を注入可能）。
    private sealed class FakeGate(CostControlGate gate) : ICostControlGate
    {
        public Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(gate);
    }

    private static ServiceProvider Build(IInformationSource source, CostControlGate? gate = null) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(source)
            .AddSingleton<IKnowledgeBaseSink, InMemoryKnowledgeBaseSink>()
            .AddSingleton(SourceAllowlist.Default)
            .AddSingleton<ICostControlGate>(new FakeGate(gate ?? CostControlGate.Normal))
            .AddScoped<AppSvc>()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);

    private static CollectionPollingService NewPolling(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CollectionOptions()),
            NullLogger<CollectionPollingService>.Instance);

    [Fact]
    public async Task 収集があれば_InformationCollected_を発行する()
    {
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "好決算", DateTimeOffset.UtcNow);
        await using var provider = Build(new StubSource([raw]));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await NewPolling(provider).RunOnceAsync(CancellationToken.None);

        (await harness.Published.Any<InformationCollected>()).Should().BeTrue();
        var published = harness.Published.Select<InformationCollected>().First().Context.Message;
        published.ItemCount.Should().Be(1);

        await harness.Stop();
    }

    [Fact]
    public async Task 収集ゼロなら発行しない()
    {
        await using var provider = Build(new NoOpInformationSource());
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await NewPolling(provider).RunOnceAsync(CancellationToken.None);

        (await harness.Published.Any<InformationCollected>()).Should().BeFalse();

        await harness.Stop();
    }

    [Fact]
    public async Task 費用統制が停止_Halted_なら収集があっても発行しない()
    {
        // NFR（費用）, IADR-0031: LLM 月次上限 100% 到達＝停止。収集/発行をスキップしてサイクルを回さない。
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "好決算", DateTimeOffset.UtcNow);
        await using var provider = Build(new StubSource([raw]), new CostControlGate(Halted: true, IntervalMultiplier: 0m));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await NewPolling(provider).RunOnceAsync(CancellationToken.None);

        (await harness.Published.Any<InformationCollected>()).Should().BeFalse();

        await harness.Stop();
    }

    // #121: fail-safe。既定トリガは InProcess（現行の in-process ポーリングを維持）。
    [Fact]
    public void 既定のトリガは_InProcess()
    {
        new CollectionOptions().Trigger.Should().Be(CollectionTrigger.InProcess);
    }

    // #121: External（本番スケジューラ=K8s CronJob）では in-process 巡回を行わない
    //（起動しても収集・発行しない。起動は run-once エンドポイント経由）。
    [Fact]
    public async Task External_モードでは_in_process_巡回を行わない()
    {
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "本文", DateTimeOffset.UtcNow);
        await using var provider = Build(new StubSource([raw]));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var svc = new CollectionPollingService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CollectionOptions { Trigger = CollectionTrigger.External, PollIntervalSeconds = 1 }),
            NullLogger<CollectionPollingService>.Instance);
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        (await harness.Published.Any<InformationCollected>()).Should().BeFalse();

        await harness.Stop();
    }

    // NFR（費用）, IADR-0031: 実効間隔の境界（Normal=base、Throttled=base×2、Halted=base×2）。
    [Fact]
    public void 実効間隔は統制状態で決まる()
    {
        var b = TimeSpan.FromSeconds(60);
        CollectionPollingService.EffectiveInterval(b, CostControlGate.Normal).Should().Be(TimeSpan.FromSeconds(60));
        CollectionPollingService.EffectiveInterval(b, new CostControlGate(false, 2m)).Should().Be(TimeSpan.FromSeconds(120));
        CollectionPollingService.EffectiveInterval(b, new CostControlGate(true, 0m)).Should().Be(TimeSpan.FromSeconds(120));
    }
}
