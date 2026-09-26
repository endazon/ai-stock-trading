using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using InformationCollectionService.Features.InformationCollection;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, FR-02, FR-13, #1015, IADR-0435: Finnhub の対象銘柄の決め方（T-10-1461〜T-10-1466）。
// 🔴 原則 A（不明は空ではない）と計画 ADR-0043 決定2 (b)（1 巡回を巡回間隔に収める）の両方を固定する。
// 計器の否定形（値の列の完全一致）を含むため、隔離した Meter 名で測る。
public class FinnhubSymbolSelectorTests
{
    private static readonly string[] Configured = ["AAPL"];

    // 読めない（不明）。
    private static readonly IReadOnlyList<WatchedSymbol>? Unknown = null;

    // T-10-1461: 読めたら米国の銘柄だけを監視銘柄の順で採る（重複は最初の 1 件・前後空白を除く・大文字小文字を区別しない）。
    [Fact]
    public async Task 読めた監視銘柄の米国の銘柄だけを監視銘柄の順で採る()
    {
        var reader = new ScriptedReader(
            [
                new WatchedSymbol("MSFT", Market.UnitedStates),
                new WatchedSymbol("7203", Market.Japan),
                new WatchedSymbol("AAPL", Market.UnitedStates),
                new WatchedSymbol(" msft ", Market.UnitedStates),
                new WatchedSymbol("META", Market.UnitedStates),
            ]);
        using var probe = new Probe(reader);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("MSFT", "AAPL", "META");
        probe.Outcomes().Should().Equal(FinnhubSymbolSelector.OutcomeWatchlist);
        probe.Logger.Warnings.Should().BeEmpty();
    }

    // T-10-1462: 読めて 0 件（または米国の銘柄が 0 件）は事実としての空。構成の固定リストへ倒さない。
    [Fact]
    public async Task 読めて米国の銘柄が0件なら空であり構成へ倒さない()
    {
        var reader = new ScriptedReader([new WatchedSymbol("7203", Market.Japan)]);
        using var probe = new Probe(reader);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().BeEmpty();
        probe.Outcomes().Should().Equal(FinnhubSymbolSelector.OutcomeWatchlist);
    }

    // T-10-1463: 読めなければ直前に読めた集合を使い続け、警告し、出所を last-known と数える。
    [Fact]
    public async Task 読めなければ直前に読めた集合を使い続け警告する()
    {
        var reader = new ScriptedReader(
            [new WatchedSymbol("NVDA", Market.UnitedStates), new WatchedSymbol("AMZN", Market.UnitedStates)],
            Unknown);
        using var probe = new Probe(reader);

        await probe.Selector.RefreshAsync();
        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("NVDA", "AMZN");
        probe.Outcomes().Should().Equal(FinnhubSymbolSelector.OutcomeWatchlist, FinnhubSymbolSelector.OutcomeLastKnown);
        probe.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("直前に読めた対象 2 件").And.Contain("NVDA,AMZN");
    }

    // T-10-1463（対）: 直前の値は読めたときだけ更新する。読めない回が挟まっても、次に読めれば新しい監視銘柄へ追随する。
    [Fact]
    public async Task 読めない回の後に読めれば新しい監視銘柄へ追随する()
    {
        var reader = new ScriptedReader(
            [new WatchedSymbol("AAPL", Market.UnitedStates)],
            Unknown,
            [new WatchedSymbol("GOOGL", Market.UnitedStates)]);
        using var probe = new Probe(reader);

        await probe.Selector.RefreshAsync();
        await probe.Selector.RefreshAsync();
        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("GOOGL");
        probe.Outcomes().Should().Equal(
            FinnhubSymbolSelector.OutcomeWatchlist, FinnhubSymbolSelector.OutcomeLastKnown, FinnhubSymbolSelector.OutcomeWatchlist);
    }

    // T-10-1464: 一度も読めていなければ構成の固定リストへ倒し、警告し、出所を configured-fallback と数える（空へ倒さない）。
    [Fact]
    public async Task 一度も読めていなければ構成の固定リストへ倒し警告する()
    {
        var reader = new ScriptedReader(Unknown);
        using var probe = new Probe(reader, configured: ["AAPL", " ", "aapl", "MSFT"]);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AAPL", "MSFT");
        probe.Outcomes().Should().Equal(FinnhubSymbolSelector.OutcomeConfiguredFallback);
        probe.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("構成の固定リスト");
    }

    // T-10-1464（対）: 構成へ倒した回を「直前に読めた集合」として覚えない（読めたときだけ更新する）。
    // 覚えると、次の失敗が「直前に読めた」と偽って数えられ、一度も追随していないことが見えなくなる。
    [Fact]
    public async Task 構成へ倒した回は直前の値として覚えない()
    {
        var reader = new ScriptedReader(Unknown);
        using var probe = new Probe(reader);

        await probe.Selector.RefreshAsync();
        await probe.Selector.RefreshAsync();

        probe.Outcomes().Should().Equal(
            FinnhubSymbolSelector.OutcomeConfiguredFallback, FinnhubSymbolSelector.OutcomeConfiguredFallback);
    }

    // T-10-1465: 収まらない分は並びの先頭から収まる数だけを採り、後回しの銘柄を警告とメトリクスで見せる。回復すれば 0 を記録する。
    [Fact]
    public async Task 巡回に収まらない分は先頭から収まる数だけを採り後回しの数を記録する()
    {
        var reader = new ScriptedReader(
            [
                new WatchedSymbol("AAPL", Market.UnitedStates),
                new WatchedSymbol("MSFT", Market.UnitedStates),
                new WatchedSymbol("NVDA", Market.UnitedStates),
                new WatchedSymbol("AMZN", Market.UnitedStates),
                new WatchedSymbol("GOOGL", Market.UnitedStates),
                new WatchedSymbol("META", Market.UnitedStates),
            ],
            [new WatchedSymbol("AAPL", Market.UnitedStates)]);
        using var probe = new Probe(reader, maxSymbols: 4);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AAPL", "MSFT", "NVDA", "AMZN");
        probe.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("GOOGL,META").And.Contain("4 銘柄まで");

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AAPL");
        // 構築時（構成 1 件・後回し 0）→ 1 回目（後回し 2）→ 2 回目（後回し 0＝回復が見える）。
        probe.Deferred().Should().Equal(0d, 2d, 0d);
    }

    // T-10-1465（境界）: ちょうど収まる数なら何も後回しにしない。
    [Fact]
    public async Task ちょうど収まる数なら後回しにしない()
    {
        var reader = new ScriptedReader(
            [new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("MSFT", Market.UnitedStates)]);
        using var probe = new Probe(reader, maxSymbols: 2);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AAPL", "MSFT");
        probe.Deferred().Should().Equal(0d, 0d);
        probe.Logger.Warnings.Should().BeEmpty();
    }

    // T-10-1466: 未結線なら照会せず、構成の固定リスト（上限つき）のまま。出所は数えない（追随していないので）。
    [Fact]
    public async Task 未結線なら照会せず構成の固定リストに上限を掛けたまま()
    {
        using var probe = new Probe(reader: null, configured: ["AAPL", "MSFT", "NVDA"], maxSymbols: 2);

        await probe.Selector.RefreshAsync();

        probe.Selector.FollowsWatchlist.Should().BeFalse();
        probe.Selector.Current.Should().Equal("AAPL", "MSFT");
        probe.Outcomes().Should().BeEmpty();
        probe.Deferred().Should().Equal(1d);
    }

    // 呼び出し側の中止は握りつぶさない（巡回ごと畳む）。
    [Fact]
    public async Task 呼び出し側の中止は伝播する()
    {
        using var probe = new Probe(new CancellingReader());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => probe.Selector.RefreshAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Selector.Current.Should().Equal("AAPL");
    }

    private sealed class Probe : IDisposable
    {
        private readonly string _meterName;
        private readonly MeterCapture _capture;
        private readonly BusinessMetrics _metrics;

        public Probe(IWatchlistReader? reader, IReadOnlyList<string>? configured = null, int maxSymbols = 100)
        {
            _meterName = MeterCapture.NewIsolatedMeterName();
            _capture = new MeterCapture(_meterName);
            _metrics = BusinessMetrics.WithMeterName(_meterName);
            Selector = new FinnhubSymbolSelector(reader, configured ?? Configured, maxSymbols, _metrics, Logger);
        }

        public CapturingLogger Logger { get; } = new();

        public FinnhubSymbolSelector Selector { get; }

        public IReadOnlyList<string> Outcomes() =>
            _capture.TagValuesOf(BusinessMetricNames.InformationCollectionFinnhubSymbolSetResolutions, BusinessMetricNames.TagOutcome);

        public IReadOnlyList<double> Deferred() =>
            [.. _capture.ValuesOf(BusinessMetricNames.InformationCollectionFinnhubSymbolsDeferred).Select(m => m.Value)];

        public void Dispose()
        {
            _metrics.Dispose();
            _capture.Dispose();
        }
    }

    // 呼ぶたびに台本の次の応答を返す（null＝読めない）。台本を使い切ったら最後の応答を返し続ける。
    private sealed class ScriptedReader(params IReadOnlyList<WatchedSymbol>?[] script) : IWatchlistReader
    {
        private int _index;

        public Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(script[Math.Min(_index++, script.Length - 1)]);
    }

    private sealed class CancellingReader : IWatchlistReader
    {
        public Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WatchedSymbol>?>([]);
        }
    }

    internal sealed class CapturingLogger : ILogger<FinnhubSymbolSelector>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _warnings.Add(formatter(state, exception));
        }
    }
}
