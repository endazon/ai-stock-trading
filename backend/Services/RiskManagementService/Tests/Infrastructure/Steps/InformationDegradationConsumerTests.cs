using RiskManagementService.Infrastructure.Persistence;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, #564, IADR-0249, IADR-0267:
// 情報収集の縮退遷移（InformationSourceDegraded / Recovered）と**現況観測**（InformationSourceStateObserved）
// → 新規建て停止状態への結線の検証。
//
// #336 は判定器とイベントを作ったが下流の結線が無く、新規建ての抑止は KB 文言による LLM の自制頼み
// だった（同仕様書が「構造的な結線は #337 の射程」と明記）。本テスト群は「イベントが実際に
// リスク管理の状態へ届くこと」と「止めるのは BlocksNewEntries=true の縮退だけであること」を固定する。
public class InformationDegradationConsumerTests
{
    private static readonly DateTimeOffset T = new(2026, 8, 28, 3, 0, 0, TimeSpan.Zero);

    private static Task<IHost> BuildHostAsync(InMemoryInformationDegradationStore store) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IInformationDegradationStore>(store);
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<InformationSourceDegradedRiskHandler>()
                    .IncludeType<InformationSourceRecoveredRiskHandler>()
                    .IncludeType<InformationSourceStateObservedRiskHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static InformationSourceDegraded Degraded(string category, bool blocksNewEntries) =>
        new(category, "LimitedDegradation", ["finnhub-news", "google-news"], blocksNewEntries, T);

    private static readonly TimeSpan Validity = TimeSpan.FromHours(1);

    // #564: 「現況を観測できていて、止めるものが無い」ストア（＝平常運転）。
    // 観測を投入しない素のストアは**再起動直後（不明）**を表し、新規建てを止める。
    private static InMemoryInformationDegradationStore ObservedHealthy(StubTimeProvider? time = null)
    {
        var store = new InMemoryInformationDegradationStore(time ?? new StubTimeProvider(T));
        store.ApplyObservation([], Validity, T.AddMinutes(-1));
        return store;
    }

    [Fact]
    public async Task 新規建て停止つき縮退イベントが状態へ畳まれる()
    {
        var store = ObservedHealthy();
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("news", blocksNewEntries: true));

        store.BlocksNewEntries.Should().BeTrue();

        await host.StopAsync();
    }

    // 否定形: BlocksNewEntries=false の縮退（記録・通知のみ／空売り限定）は新規建てを止めない。
    // イベント自身の宣言に従い、受け手が Behavior を再解釈して停止範囲を広げない。
    [Fact]
    public async Task 新規建て停止なしの縮退イベントは状態を変えない()
    {
        var store = ObservedHealthy();
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("macro", blocksNewEntries: false));

        store.BlocksNewEntries.Should().BeFalse();

        await host.StopAsync();
    }

    [Fact]
    public async Task 回復イベントで新規建て停止が解ける()
    {
        var store = ObservedHealthy();
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("news", blocksNewEntries: true));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationSourceRecovered("news", T, AffectedCycles: 3, T.AddHours(1)));

        store.BlocksNewEntries.Should().BeFalse();

        await host.StopAsync();
    }

    [Fact]
    public async Task 複数カテゴリの縮退は全カテゴリが回復するまで停止が続く()
    {
        var store = ObservedHealthy();
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("news", blocksNewEntries: true));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("disclosure-us", blocksNewEntries: true));
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationSourceRecovered("news", T, AffectedCycles: 1, T.AddMinutes(30)));

        store.BlocksNewEntries.Should().BeTrue("disclosure-us がまだ回復していない");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationSourceRecovered("disclosure-us", T, AffectedCycles: 2, T.AddHours(1)));

        store.BlocksNewEntries.Should().BeFalse();

        await host.StopAsync();
    }

    // ------------------------------------------------------------------
    // #564: 現況観測（再起動を跨いだ復元）
    // ------------------------------------------------------------------

    // 🔴 **受け入れ基準①。** 再起動を模した**空のストア**へ、**遷移イベントを 1 件も与えず**
    // 現況観測だけを届けると、新規建ての停止が復元される。
    [Fact]
    public async Task 遷移が無くても現況観測だけで新規建ての停止が復元される()
    {
        var store = new InMemoryInformationDegradationStore(new StubTimeProvider(T)); // 再起動直後（未観測）
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationSourceStateObserved(["news"], Validity, T));

        store.BlocksNewEntries.Should().BeTrue();

        await host.StopAsync();
    }

    // 対の肯定形: 現況が「止めるものは無い」なら停止は解ける（恒久停止にしない）。
    [Fact]
    public async Task 健全な現況観測を受け取れば新規建ては再開する_対の肯定形()
    {
        var store = new InMemoryInformationDegradationStore(new StubTimeProvider(T));
        using var host = await BuildHostAsync(store);

        store.BlocksNewEntries.Should().BeTrue("観測が届くまでは不明＝止める");

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationSourceStateObserved([], Validity, T));

        store.BlocksNewEntries.Should().BeFalse();

        await host.StopAsync();
    }

    // 現況観測は**全量**であり、受け手は集合を置き換える（遷移の取りこぼしが残り続けない）。
    [Fact]
    public async Task 現況観測は停止カテゴリを全量で置き換える()
    {
        var store = ObservedHealthy();
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("news", blocksNewEntries: true));
        store.BlocksNewEntries.Should().BeTrue();

        // 収集側は既に回復を観測している（回復の遷移を取りこぼしていても、現況が真実を運ぶ）。
        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(
            new InformationSourceStateObserved([], Validity, T.AddMinutes(30)));

        store.BlocksNewEntries.Should().BeFalse();

        await host.StopAsync();
    }
}
