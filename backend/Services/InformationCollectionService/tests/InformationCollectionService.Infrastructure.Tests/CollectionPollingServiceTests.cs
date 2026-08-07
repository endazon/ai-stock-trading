using AiStockTrading.InformationCollection.Application.Adapters;
using AiStockTrading.InformationCollection.Application.Ports;
using AiStockTrading.InformationCollection.Application.State;
using AiStockTrading.InformationCollection.Domain;
using AiStockTrading.InformationCollection.Infrastructure.Composable.Polling;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = AiStockTrading.InformationCollection.Application.Services.InformationCollectionService;

namespace AiStockTrading.InformationCollection.Infrastructure.Tests;

// FR-01, FR-02: 1 巡回で収集→保存し、収集があれば InformationCollected を発行することを検証する。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Published）から
// Wolverine.Tracking（TrackActivity + session.Sent）へ移行した。表明の意味は同じ（巡回を回し、外へ出た／
// 出なかったメッセージを見る）。実ブローカへは接続しない（StubAllExternalTransports）。
public class CollectionPollingServiceTests
{
    private const string ServiceName = "ai-stock-trading.information-collection-service";

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

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildAsync(IInformationSource source, CostControlGate? gate = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(source);
                opts.Services.AddSingleton<IKnowledgeBaseSink, InMemoryKnowledgeBaseSink>();
                opts.Services.AddSingleton(SourceAllowlist.Default);
                opts.Services.AddSingleton<ICostControlGate>(new FakeGate(gate ?? CostControlGate.Normal));
                opts.Services.AddScoped<AppSvc>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static CollectionPollingService NewPolling(IHost host) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CollectionOptions()),
            NullLogger<CollectionPollingService>.Instance);

    [Fact]
    public async Task 収集があれば_InformationCollected_を発行する()
    {
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "好決算", DateTimeOffset.UtcNow);
        using var host = await BuildAsync(new StubSource([raw]));

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<InformationCollected>().Should().NotBeEmpty();
        var published = session.Sent.MessagesOf<InformationCollected>().First();
        published.ItemCount.Should().Be(1);

        // IADR-0129 決定 2: 宛先はメッセージ型ごとの共有 fanout exchange（購読側サービスがここに bind する）。
        session.Sent.Envelopes().Should().Contain(e =>
            e.Message is InformationCollected
            && e.Destination!.ToString()
                == "rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.InformationCollected");

        await host.StopAsync();
    }

    [Fact]
    public async Task 収集ゼロなら発行しない()
    {
        using var host = await BuildAsync(new NoOpInformationSource());

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<InformationCollected>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 費用統制が停止_Halted_なら収集があっても発行しない()
    {
        // NFR（費用）, IADR-0031: LLM 月次上限 100% 到達＝停止。収集/発行をスキップしてサイクルを回さない。
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "好決算", DateTimeOffset.UtcNow);
        using var host = await BuildAsync(new StubSource([raw]), new CostControlGate(Halted: true, IntervalMultiplier: 0m));

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<InformationCollected>().Should().BeEmpty();

        await host.StopAsync();
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
        using var host = await BuildAsync(new StubSource([raw]));

        var svc = new CollectionPollingService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CollectionOptions { Trigger = CollectionTrigger.External, PollIntervalSeconds = 1 }),
            NullLogger<CollectionPollingService>.Instance);

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => RunBrieflyAsync(svc));

        session.Sent.MessagesOf<InformationCollected>().Should().BeEmpty();

        await host.StopAsync();
    }

    // 常駐として少しだけ起動して止める（External では巡回しないことの確認用）。
    private static async Task RunBrieflyAsync(CollectionPollingService svc)
    {
        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);
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
