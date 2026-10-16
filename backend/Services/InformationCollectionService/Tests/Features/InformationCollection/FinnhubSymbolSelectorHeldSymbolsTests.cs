using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Metrics;
using InformationCollectionService.Features.InformationCollection;
using AwesomeAssertions;
using Xunit;

namespace InformationCollectionService.Tests;

// 🔴 FR-01, FR-02, #1363, IADR-0547 決定 1: 収集の対象（Finnhub の現在値・企業ニュース）へ保有銘柄を合流させる。
// 取引判断は「監視銘柄 ∪ 保有銘柄」を判断する（IADR-0521 決定 1）ため、監視銘柄の外の保有銘柄もニュースを集める。
// 上限（ADR-0043 決定 2 (b)）を超えるときは保有を先に採り、保有を読めない巡回は監視銘柄だけへ縮退して警告する。
public class FinnhubSymbolSelectorHeldSymbolsTests
{
    private static readonly IReadOnlyList<WatchedSymbol> Watchlist =
    [
        new("NVDA", Market.UnitedStates),
        new("META", Market.UnitedStates),
        new("TSLA", Market.UnitedStates),
    ];

    // 保有が対象に入る。保有が先、続いて監視銘柄（監視銘柄にも在る保有は 1 件だけ）。日本の保有は入れない（Finnhub は米国株）。
    [Fact]
    public async Task 保有銘柄が対象に入り保有が先に並ぶ()
    {
        var held = new ScriptedHeld(
            [
                new WatchedSymbol("AAPL", Market.UnitedStates),
                new WatchedSymbol("META", Market.UnitedStates),
                new WatchedSymbol("7203", Market.Japan),
                new WatchedSymbol("GOOGL", Market.UnitedStates),
            ]);
        using var probe = new Probe(new FixedReader(Watchlist), held);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AAPL", "META", "GOOGL", "NVDA", "TSLA");
        probe.Selector.UnitedStatesTargets.Should().Equal("AAPL", "META", "GOOGL", "NVDA", "TSLA");
        probe.Logger.Warnings.Should().BeEmpty();
        probe.Logger.Infos.Should().ContainSingle(m => m.StartsWith("監視銘柄から Finnhub の対象を決めました: 米国の銘柄 5 件", StringComparison.Ordinal))
            .Which.Should().Contain("監視銘柄 3 件").And.Contain("監視銘柄の外の保有 2 件");
    }

    // 上限を超えるときは保有を先に採る（後回しにされるのは監視銘柄の末尾）。上限を掛ける前の対象は SEC 用に全件を見せる。
    [Fact]
    public async Task 上限を超えるときは保有を先に採る()
    {
        var held = new ScriptedHeld([new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("AMZN", Market.UnitedStates)]);
        using var probe = new Probe(new FixedReader(Watchlist), held, maxSymbols: 3);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AAPL", "AMZN", "NVDA");
        probe.Selector.UnitedStatesTargets.Should().Equal("AAPL", "AMZN", "NVDA", "META", "TSLA");
        probe.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("META,TSLA").And.Contain("監視銘柄と保有銘柄");
    }

    // 保有を読めない（null）巡回は監視銘柄だけから決め、警告する。直前に読めた保有を使い続けない（IADR-0521 決定 1 と同じ）。
    [Fact]
    public async Task 保有を読めないときは監視銘柄だけに縮退して警告する()
    {
        var held = new ScriptedHeld([new WatchedSymbol("AAPL", Market.UnitedStates)], null);
        using var probe = new Probe(new FixedReader(Watchlist), held);

        await probe.Selector.RefreshAsync();
        probe.Selector.Current.Should().Equal("AAPL", "NVDA", "META", "TSLA");

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("NVDA", "META", "TSLA");
        probe.Selector.UnitedStatesTargets.Should().Equal("NVDA", "META", "TSLA");
        probe.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("保有銘柄を読めなかった").And.Contain("監視銘柄だけ");
    }

    // 保有の口が例外を投げても巡回を落とさず、監視銘柄だけへ縮退する（IADR-0521 決定 5 と同じ）。
    [Fact]
    public async Task 保有の照会が例外でも監視銘柄だけに縮退する()
    {
        using var probe = new Probe(new FixedReader(Watchlist), new ThrowingHeld());

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("NVDA", "META", "TSLA");
        probe.Logger.Warnings.Should().ContainSingle().Which.Should().Contain("保有銘柄の照会で例外");
    }

    // 呼び出し側の中止は保有の照会でも握りつぶさない。
    [Fact]
    public async Task 保有の照会で呼び出し側の中止は伝播する()
    {
        using var probe = new Probe(new FixedReader(Watchlist), new CancellingHeld());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => probe.Selector.RefreshAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // 監視銘柄を読めない巡回でも、保有は合流させる（直前の監視銘柄 ＋ 今回の保有）。
    [Fact]
    public async Task 監視銘柄を読めない巡回でも保有は合流させる()
    {
        var reader = new ScriptedReader(Watchlist, null);
        var held = new ScriptedHeld([], [new WatchedSymbol("AMZN", Market.UnitedStates)]);
        using var probe = new Probe(reader, held);

        await probe.Selector.RefreshAsync();
        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("AMZN", "NVDA", "META", "TSLA");
    }

    // 保有の口が未結線なら従来どおり（監視銘柄だけ・警告なし）。
    [Fact]
    public async Task 保有の口が未結線なら監視銘柄だけで警告しない()
    {
        using var probe = new Probe(new FixedReader(Watchlist), held: null);

        await probe.Selector.RefreshAsync();

        probe.Selector.Current.Should().Equal("NVDA", "META", "TSLA");
        probe.Logger.Warnings.Should().BeEmpty();
    }

    private sealed class Probe : IDisposable
    {
        private readonly MeterCapture _capture;
        private readonly BusinessMetrics _metrics;

        public Probe(IWatchlistReader reader, IHeldSymbolsProvider? held, int maxSymbols = 100)
        {
            var meterName = MeterCapture.NewIsolatedMeterName();
            _capture = new MeterCapture(meterName);
            _metrics = BusinessMetrics.WithMeterName(meterName);
            Selector = new FinnhubSymbolSelector(reader, ["AAPL"], maxSymbols, _metrics, Logger, held);
        }

        public CapturingLogger Logger { get; } = new();

        public FinnhubSymbolSelector Selector { get; }

        public void Dispose()
        {
            _metrics.Dispose();
            _capture.Dispose();
        }
    }

    private sealed class FixedReader(IReadOnlyList<WatchedSymbol> watchlist) : IWatchlistReader
    {
        public Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchedSymbol>?>(watchlist);
    }

    private sealed class ScriptedReader(params IReadOnlyList<WatchedSymbol>?[] script) : IWatchlistReader
    {
        private int _index;

        public Task<IReadOnlyList<WatchedSymbol>?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(script[Math.Min(_index++, script.Length - 1)]);
    }

    // 呼ぶたびに台本の次の応答を返す（null＝読めない）。台本を使い切ったら最後の応答を返し続ける。
    private sealed class ScriptedHeld(params IReadOnlyList<WatchedSymbol>?[] script) : IHeldSymbolsProvider
    {
        private int _index;

        public Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(script[Math.Min(_index++, script.Length - 1)]);
    }

    private sealed class ThrowingHeld : IHeldSymbolsProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CancellingHeld : IHeldSymbolsProvider
    {
        public Task<IReadOnlyList<WatchedSymbol>?> GetHeldSymbolsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<WatchedSymbol>?>([]);
        }
    }

    internal sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<FinnhubSymbolSelector>
    {
        private readonly List<string> _warnings = [];
        private readonly List<string> _infos = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IReadOnlyList<string> Infos => _infos;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                _warnings.Add(formatter(state, exception));
            else if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information)
                _infos.Add(formatter(state, exception));
        }
    }
}
