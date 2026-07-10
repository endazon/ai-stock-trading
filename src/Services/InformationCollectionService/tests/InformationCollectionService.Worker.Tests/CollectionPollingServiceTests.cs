using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Worker.Composable.Polling;
using AiStockTrading.Shared.Contracts.Events;
using FluentAssertions;
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

    private static ServiceProvider Build(IInformationSource source) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(source)
            .AddSingleton<IKnowledgeBaseSink, InMemoryKnowledgeBaseSink>()
            .AddSingleton(SourceAllowlist.Default)
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
}
