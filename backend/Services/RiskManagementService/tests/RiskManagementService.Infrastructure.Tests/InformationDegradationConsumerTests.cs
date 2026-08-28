using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.RiskManagement.Infrastructure.Tests;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, IADR-0249:
// 情報収集の縮退遷移（InformationSourceDegraded / Recovered）→ 新規建て停止状態への結線の検証。
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
                    .IncludeType<InformationSourceRecoveredRiskHandler>();
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static InformationSourceDegraded Degraded(string category, bool blocksNewEntries) =>
        new(category, "LimitedDegradation", ["finnhub-news", "google-news"], blocksNewEntries, T);

    [Fact]
    public async Task 新規建て停止つき縮退イベントが状態へ畳まれる()
    {
        var store = new InMemoryInformationDegradationStore();
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
        var store = new InMemoryInformationDegradationStore();
        using var host = await BuildHostAsync(store);

        await host.TrackActivityForTest().InvokeMessageAndWaitAsync(Degraded("macro", blocksNewEntries: false));

        store.BlocksNewEntries.Should().BeFalse();

        await host.StopAsync();
    }

    [Fact]
    public async Task 回復イベントで新規建て停止が解ける()
    {
        var store = new InMemoryInformationDegradationStore();
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
        var store = new InMemoryInformationDegradationStore();
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
}
