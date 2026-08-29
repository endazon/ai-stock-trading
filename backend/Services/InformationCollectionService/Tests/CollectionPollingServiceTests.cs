using InformationCollectionService.Common.Abstractions;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using InformationCollectionService.Hosted;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Tracking;
using Xunit;
using AppSvc = InformationCollectionService.Features.InformationCollection.InformationCollectionAppService;

namespace InformationCollectionService.Tests;

// FR-01, FR-02: 1 巡回で収集→保存し、収集があれば InformationCollected を発行することを検証する。
//
// ADR-0013, IADR-0129, #354: MassTransit のテストハーネス（AddMassTransitTestHarness + harness.Published）から
// Wolverine.Tracking（TrackActivity + session.Sent）へ移行した。表明の意味は同じ（巡回を回し、外へ出た／
// 出なかったメッセージを見る）。実ブローカへは接続しない（StubAllExternalTransports）。
public class CollectionPollingServiceTests
{
    private const string ServiceName = "ai-stock-trading.information-collection-service";

    // ソース単位の成否つきの取得結果を返すフェイク（ADR-0020 決定3 の判定入力）。
    private sealed class StubFetcher(SourceFetchResult result) : ISourceFetcher
    {
        public StubFetcher(IReadOnlyList<RawInformationItem> items)
            : this(new SourceFetchResult(items, [.. items.Select(i => SourceOutcome.Ok(i.Source)).Distinct()]))
        {
        }

        public Task<SourceFetchResult> FetchAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    // 費用統制ゲートのフェイク（既定 Normal・Halted を注入可能）。
    private sealed class FakeGate(CostControlGate gate) : ICostControlGate
    {
        public Task<CostControlGate> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(gate);
    }

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildAsync(ISourceFetcher fetcher, CostControlGate? gate = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(fetcher);
                opts.Services.AddSingleton<IKnowledgeBaseSink, InMemoryKnowledgeBaseSink>();
                opts.Services.AddSingleton(SourceAllowlist.Default);
                opts.Services.AddSingleton(InformationSourceCatalog.Default);
                opts.Services.AddSingleton<IClock>(new StubClock());
                opts.Services.AddSingleton<ICostControlGate>(new FakeGate(gate ?? CostControlGate.Normal));
                opts.Services.AddScoped<AppSvc>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static CollectionPollingService NewPolling(IHost host, BusinessMetrics? metrics = null) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CollectionOptions()),
            metrics ?? new BusinessMetrics(),
            NullLogger<CollectionPollingService>.Instance);

    [Fact]
    public async Task 収集があれば_InformationCollected_を発行する()
    {
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "好決算", DateTimeOffset.UtcNow);
        using var host = await BuildAsync(new StubFetcher([raw]));

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
        using var host = await BuildAsync(new NoSourcesFetcher());

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
        using var host = await BuildAsync(new StubFetcher([raw]), new CostControlGate(Halted: true, IntervalMultiplier: 0m));

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
        using var host = await BuildAsync(new StubFetcher([raw]));

        var svc = new CollectionPollingService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CollectionOptions { Trigger = CollectionTrigger.External, PollIntervalSeconds = 1 }),
            new BusinessMetrics(),
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

    // 🔴 FR-01, #336, ADR-0020 決定3: **サイクル中止**（必須ソースの欠測）では取引サイクルを起こさない。
    // 止まるのは**新規の判断サイクル**だけであり、手仕舞い・損切りは別経路（ブローカ側の逆指値・NFR-04）である。
    [Fact]
    public async Task サイクル中止の欠測では_InformationCollected_を発行しない()
    {
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "好決算", DateTimeOffset.UtcNow);
        var fetcher = new StubFetcher(new SourceFetchResult(
            [raw], [SourceOutcome.Ok("finnhub"), SourceOutcome.Failed("moomoo")]));
        using var host = await BuildAsync(fetcher);

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<InformationCollected>().Should().BeEmpty("必須情報源の欠測でサイクルを中止する");
        session.Sent.MessagesOf<InformationSourceDegraded>().Should().ContainSingle()
            .Which.Behavior.Should().Be(nameof(MissingSourceBehavior.AbortCycle));

        await host.StopAsync();
    }

    // 🔴 **限定縮退ではサイクルを止めない。** 止めるのは新規建てだけであり、収集も判断も続く（ADR-0020 決定2）。
    [Fact]
    public async Task ニュース系の全滅では縮退を通知しつつサイクルは継続する()
    {
        var raw = new RawInformationItem(InformationKind.Quote, "finnhub", "AAPL", "現在値", "current=1", DateTimeOffset.UtcNow);
        var fetcher = new StubFetcher(new SourceFetchResult(
            [raw],
            [SourceOutcome.Ok("finnhub"), SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news")]));
        using var host = await BuildAsync(fetcher);

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        session.Sent.MessagesOf<InformationCollected>().Should().NotBeEmpty("限定縮退はサイクルを止めない");
        var degraded = session.Sent.MessagesOf<InformationSourceDegraded>().Should().ContainSingle().Which;
        degraded.Category.Should().Be(InformationSourceCatalog.NewsCategory);
        degraded.BlocksNewEntries.Should().BeTrue();
        degraded.ClosesAllowed.Should().BeTrue("手仕舞いは止めない");

        await host.StopAsync();
    }

    // 遷移でのみ発行する（続いている間は黙る）。1 巡回で N 件出る洪水を作らない。
    [Fact]
    public async Task 欠測が続いている間は再発行しない()
    {
        var fetcher = new StubFetcher(new SourceFetchResult(
            [], [SourceOutcome.Failed("finnhub-news"), SourceOutcome.Failed("google-news")]));
        using var host = await BuildAsync(fetcher);
        var polling = NewPolling(host);

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => RunTwiceAsync(polling));

        session.Sent.MessagesOf<InformationSourceDegraded>().Should().HaveCount(1);

        await host.StopAsync();
    }

    // 2 巡回を 1 つの追跡セッションで回す（ラムダの戻り値型が曖昧にならないよう明示的な Task メソッドにする）。
    private static async Task RunTwiceAsync(CollectionPollingService polling)
    {
        await polling.RunOnceAsync(CancellationToken.None);
        await polling.RunOnceAsync(CancellationToken.None);
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

    // NFR-07, FR-01, FR-02, #287, IADR-0255: 取引サイクルの起点（収集）が動いていることが見える（肯定形）。
    [Fact]
    public async Task 収集件数が業務メトリクスへ実際に刻まれる()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        var raw = new RawInformationItem(InformationKind.News, "finnhub", "AAPL", "見出し", "本文", DateTimeOffset.UtcNow);
        using var host = await BuildAsync(new StubFetcher([raw]));

        await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        capture.SumOf(BusinessMetricNames.InformationItemsCollected).Should().BeGreaterThan(0);

        await host.StopAsync();
    }

    // NFR-07, #287, IADR-0255: **空巡回（0 件）でも計上する**（対の肯定形）。
    // 0 を計上しないと「巡回が回って 0 件だった」と「巡回そのものが止まっている」がどちらも
    // 「カウンタが伸びない」という同じ形になり、区別できない。
    [Fact]
    public async Task 空巡回でも収集件数の計器が発火する()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        using var host = await BuildAsync(new StubFetcher([]));

        await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => NewPolling(host).RunOnceAsync(CancellationToken.None));

        capture.ValuesOf(BusinessMetricNames.InformationItemsCollected)
            .Should().Contain(m => m.Value == 0d, "0 件の巡回も 1 件の測定値として出る");

        await host.StopAsync();
    }
}
