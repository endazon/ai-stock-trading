using BacktestService.Domain;
using BacktestService.Features.Backtest;
using BacktestService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
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

namespace BacktestService.Tests;

// FR-15, FR-20, ADR-0008, ADR-0033, #688, IADR-0310: Stage 0 判定の定時駆動の検証。
//
// 本番と同じ配線（共通ヘルパのキュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す
// （実 RabbitMQ は使わない。実ブローカ疎通は #82）。見るのは 3 点である。
//   1. 駆動経路が実行され BacktestEvaluated が 1 通発行される
//   2. **否定形（最重要）**: 過去データが空のとき合格 verdict を出さない（判定を走らせない）
//   3. **否定形**: プレースホルダの verdict は本番の合否ではない（Passed=false 固定・理由が読める）
public class Stage0EvaluationServiceTests
{
    private const string ServiceName = "ai-stock-trading.backtest-service";
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    private static Task<IHost> StartHostAsync(IHistoricalBarSource source) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(source);
                // 本サービスはハンドラを持たない（発行専用）。テスト土台の型を拾わないよう規約発見を止める。
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static Stage0EvaluationService Driver(IHost host, Stage0EvaluationOptions options) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            new FixedTimeProvider(Now),
            NullLogger<Stage0EvaluationService>.Instance);

    private static Stage0EvaluationOptions Options_(bool enabled = true, string? cutoff = null) => new()
    {
        Enabled = enabled,
        IntervalSeconds = 60,
        LookbackDays = 30,
        LlmTrainingCutoff = cutoff,
        Symbols = [new Stage0EvaluationOptions.SymbolEntry { Symbol = "AAPL", Market = Market.UnitedStates }],
    };

    private static PriceBar Bar(DateOnly date, decimal close) =>
        new("AAPL", Market.UnitedStates, date, close, close, close, close, 1_000);

    // 🔴 **否定形（最重要）**: 過去データが 0 本のとき、合格 verdict は絶対に出ない。
    [Fact]
    public async Task 過去データが空なら判定を走らせず不合格verdictを発行する_failclosed()
    {
        // FR-15, ADR-0008, #688: 既定の provider は none（外部へ 1 リクエストも出さない）であるため、
        // 有効化直後の実運用は常にこの経路である。DataCutoffPolicy は空を違反と見なさない（真空的に真）ので、
        // 判定器へ通してはならない。
        using var host = await StartHostAsync(new StubBarSource([]));
        var driver = Driver(host, Options_());

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => driver.RunOnceAsync(CancellationToken.None));

        var verdict = session.Sent.MessagesOf<BacktestEvaluated>().Should().ContainSingle().Which;
        verdict.Passed.Should().BeFalse();
        verdict.FailedChecks.Should().Contain(nameof(Stage0GateCheck.NoHistoricalBars));
        verdict.MaxDrawdownRatio.Should().Be(0m);

        await host.StopAsync();
    }

    // 駆動経路が実行され、verdict が 1 通発行される（バーがある場合）。
    [Fact]
    public async Task バーがあれば走行して不合格固定のverdictを発行する()
    {
        // FR-15, FR-20, ADR-0033, #688: 走行そのものは行う（取得→シミュレーション→写像→発行が動くことの確認）。
        // ただし評価対象はプレースホルダであり、**verdict は本番の合否ではない**。
        var bars = new List<PriceBar>
        {
            Bar(new DateOnly(2026, 9, 1), 100m),
            Bar(new DateOnly(2026, 9, 2), 101m),
            Bar(new DateOnly(2026, 9, 3), 99m),
        };
        using var host = await StartHostAsync(new StubBarSource(bars));
        var driver = Driver(host, Options_(cutoff: "2026-08-31"));

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => driver.RunOnceAsync(CancellationToken.None));

        var verdict = session.Sent.MessagesOf<BacktestEvaluated>().Should().ContainSingle().Which;
        verdict.Passed.Should().BeFalse();
        verdict.FailedChecks.Should().Contain(nameof(Stage0GateCheck.PlaceholderStrategy));
        // 全バーがカットオフ後なので、検証条件①は未達理由に載らない（理由の読み違えを作らない）。
        verdict.FailedChecks.Should().NotContain(nameof(Stage0GateCheck.DataCutoff));
        verdict.StrategyId.Should().Be(PlaceholderStrategy.StrategyId);
        // プレースホルダは注文を出さないため空売りの観測も false（実弾の空売り解禁へは効かない）。
        verdict.IncludesShortSelling.Should().BeFalse();
        verdict.EvaluatedAt.Should().Be(Now);

        await host.StopAsync();
    }

    // **否定形**: カットオフ日が未構成なら、検証条件①を満たしたと名乗らない。
    [Fact]
    public async Task カットオフ日が未構成ならデータカットオフを未達として載せる()
    {
        // ADR-0033 決定3: 汚染対策はカットオフ後データを原則とする。カットオフ日の供給元は計画側に未登録であり、
        // 未設定を「充足」に倒すと、確認していない条件を満たしたように読める。
        using var host = await StartHostAsync(new StubBarSource([Bar(new DateOnly(2026, 9, 1), 100m)]));
        var driver = Driver(host, Options_(cutoff: null));

        var session = await host.TrackActivityForTest()
            .ExecuteAndWaitAsync(_ => driver.RunOnceAsync(CancellationToken.None));

        var verdict = session.Sent.MessagesOf<BacktestEvaluated>().Should().ContainSingle().Which;
        verdict.FailedChecks.Should().Contain(nameof(Stage0GateCheck.DataCutoff));

        await host.StopAsync();
    }

    // 境界値テーブル: 評価期間は当日から LookbackDays 遡った範囲であり、取得要求もその範囲で出る。
    [Theory]
    [InlineData(1, "2026-09-08")]
    [InlineData(30, "2026-08-10")]
    [InlineData(0, "2026-09-08")]   // 0 以下は 1 日へクランプ（期間なしの取得要求を出さない）
    [InlineData(-5, "2026-09-08")]
    public async Task 評価期間は当日から遡った範囲になる(int lookbackDays, string expectedFrom)
    {
        // FR-15, #688: 取得は 1 回だけ・期間は決定的（走行のたびに範囲が揺れない）。
        var source = new StubBarSource([]);
        using var host = await StartHostAsync(source);
        var options = Options_();
        options.LookbackDays = lookbackDays;
        var driver = Driver(host, options);

        await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => driver.RunOnceAsync(CancellationToken.None));

        source.LastFrom.Should().Be(DateOnly.Parse(expectedFrom));
        source.LastTo.Should().Be(new DateOnly(2026, 9, 9));

        await host.StopAsync();
    }

    // 🔴 **否定形（fail-safe）**: 既定（無効）では常駐が 1 度も巡回しない。
    [Fact]
    public async Task 既定は無効で常駐は巡回もデータ取得も行わない()
    {
        // FR-15, #688, IADR-0310 決定1: 明示的に有効化するまで、外部への要求も verdict の発行も起きない。
        var source = new StubBarSource([]);
        using var host = await StartHostAsync(source);
        var driver = Driver(host, Options_(enabled: false));

        await driver.StartAsync(CancellationToken.None);
        await driver.StopAsync(CancellationToken.None);

        source.CallCount.Should().Be(0);

        await host.StopAsync();
    }

    // 有効化すると常駐が起動時に 1 巡回する（駆動が「常駐として」動くことの担保）。
    [Fact]
    public async Task 有効化すると常駐が起動時に1巡回する()
    {
        // FR-15, #688: 構成で明示的に有効化したときだけ走る（既定無効の対（つい）となる肯定形）。
        var source = new StubBarSource([]);
        using var host = await StartHostAsync(source);
        // 常駐ループの間隔は下限 60 秒へクランプされるため、観測するのは起動直後の 1 巡回だけである。
        var driver = new Stage0EvaluationService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(Options_()),
            TimeProvider.System,
            NullLogger<Stage0EvaluationService>.Instance);

        await driver.StartAsync(CancellationToken.None);
        await source.FirstCall.WaitAsync(TimeSpan.FromSeconds(30));
        await driver.StopAsync(CancellationToken.None);

        source.CallCount.Should().BeGreaterThan(0);

        await host.StopAsync();
    }

    // 過去データ源の最小の偽装（要求された期間と回数を記録する）。
    private sealed class StubBarSource(IReadOnlyList<PriceBar> bars) : IHistoricalBarSource
    {
        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public DateOnly LastFrom { get; private set; }

        public DateOnly LastTo { get; private set; }

        public Task FirstCall => _firstCall.Task;

        public Task<HistoricalBarLoad> LoadBarsAsync(
            IReadOnlyList<(string Symbol, Market Market)> symbols,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastFrom = from;
            LastTo = to;
            _firstCall.TrySetResult();
            return Task.FromResult(new HistoricalBarLoad(bars, []));
        }
    }

    // TimeProvider の最小の偽装（FakeTimeProvider は中央パッケージ管理に未登録。IADR-0064/0066 と同じ理由）。
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
