using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// FR-10, #257, IADR-0107 決定5: FX レート源の選択。既定・構成不備はすべて no-op（実接続しない）へ倒す
// （MarketDataSourceFactory・IADR-0068 と同形）。
public class FxRateSourceFactoryTests
{
    [Fact]
    public void 既定_Provider未設定_は実接続しないno_op()
    {
        Create(new FxOptions()).Should().BeOfType<NoOpFxRateSource>();
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData("   ")]
    public void noneや空指定は実接続しないno_op(string provider)
    {
        Create(new FxOptions { Provider = provider }).Should().BeOfType<NoOpFxRateSource>();
    }

    [Fact]
    public void 未知のproviderは実接続しないno_op()
    {
        // 起動は落とさない。レート無し＝非基準通貨の新規建て見送り（安全側）へ縮退する。
        Create(new FxOptions { Provider = "openexchangerates" }).Should().BeOfType<NoOpFxRateSource>();
    }

    [Fact]
    public void fred指定でもAPIキーが無ければ実接続しないno_op()
    {
        Create(new FxOptions { Provider = "fred" }).Should().BeOfType<NoOpFxRateSource>();
    }

    [Theory]
    [InlineData("fred")]
    [InlineData("Fred")]
    [InlineData(" FRED ")]
    public void fred指定かつAPIキーありで実レート源になる(string provider)
    {
        var source = Create(new FxOptions
        {
            Provider = provider,
            Fred = new FredFxOptions { ApiKey = "key" },
        });

        // 実レート源は TTL・鮮度上限の装飾を必ず経由する（生の取得器を直に配らない）。
        source.Should().BeOfType<CachingFxRateSource>();
    }

    [Fact]
    public void 選択中のproviderを自己申告する()
    {
        // 「有効化したつもりで効いていない」を introspection で検知できるよう、Create と同じ規則で解決する。
        FxRateSourceFactory.ResolveProvider(new FxOptions()).Should().Be("none");
        FxRateSourceFactory.ResolveProvider(new FxOptions { Provider = "fred" }).Should().Be("none");
        FxRateSourceFactory
            .ResolveProvider(new FxOptions { Provider = "fred", Fred = new FredFxOptions { ApiKey = "key" } })
            .Should().Be("fred");
    }

    // FR-10, #271, IADR-0112 決定1: 既定の鮮度上限はデータ源の公表周期（DEXJPUS＝H.10 週次リリース）から導く。
    // 内訳: 公表間隔 7 日 ＋ 公表ラグ（金→月）3 日 ＋ 祝日ずれ 2 日 ＋ 公表時刻 ≒ 12.84 日 に約 1.2 日の余裕。
    [Fact]
    public void 既定の鮮度上限は公表周期から導いた14日()
    {
        FxRateSourceFactory.ResolveMaxRateAge(new FxOptions()).Should().Be(TimeSpan.FromDays(14));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ゼロ以下の鮮度上限は既定へ倒す(int days)
    {
        // 構成ミスで「無制限」にはしない（歯止めを失わない）。
        FxRateSourceFactory.ResolveMaxRateAge(new FxOptions { MaxRateAgeDays = days })
            .Should().Be(TimeSpan.FromDays(FxOptions.DefaultMaxRateAgeDays));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(365)]
    public void 上限を超える鮮度指定はクランプする(int days)
    {
        // IADR-0112 決定2: 週次公表が 4 回以上連続で落ちる事態は公表周期では説明できない。「動かないので 365 に
        // する」といった運用の逃げ道で鮮度 guard を実質無効化させない（設定値ではなく構造で担保する）。
        FxRateSourceFactory.ResolveMaxRateAge(new FxOptions { MaxRateAgeDays = days })
            .Should().Be(TimeSpan.FromDays(FxOptions.MaxAllowedRateAgeDays));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(31)]
    public void 範囲内の鮮度指定はそのまま尊重する(int days)
    {
        FxRateSourceFactory.ResolveMaxRateAge(new FxOptions { MaxRateAgeDays = days })
            .Should().Be(TimeSpan.FromDays(days));
    }

    [Fact]
    public void 上限を超える鮮度指定は丸めたことを警告する()
    {
        // 丸めが黙って起きると「365 日に設定したのに見送られる」が説明不能になる（＝クランプの意図である
        // 「緩めようとした操作に気づける」が失われる）。IADR-0112 決定2。
        var logs = new CapturingLoggerFactory();

        _ = Create(Fred(days: 365), logs);

        logs.Warnings.Should().ContainSingle(message =>
            message.Contains("Fx:MaxRateAgeDays", StringComparison.Ordinal)
            && message.Contains("365", StringComparison.Ordinal));
    }

    [Fact]
    public void 範囲内の鮮度指定では丸めの警告を出さない()
    {
        var logs = new CapturingLoggerFactory();

        _ = Create(Fred(days: FxOptions.MaxAllowedRateAgeDays), logs);

        logs.Warnings.Should().BeEmpty();
    }

    private static FxOptions Fred(int days) => new()
    {
        Provider = "fred",
        MaxRateAgeDays = days,
        Fred = new FredFxOptions { ApiKey = "key" },
    };

    [Fact]
    public async Task no_opは外貨のレートを解決しない()
    {
        var source = new NoOpFxRateSource(NullLogger<NoOpFxRateSource>.Instance);

        (await source.GetRateToBaseAsync(Currency.Usd)).Should().BeNull();
    }

    [Fact]
    public async Task no_opでも基準通貨はレート1で解決する()
    {
        // FX 未有効化の環境でも基準通貨（日本株）は従来どおり取引できる＝影響を非基準通貨に限定する（IADR-0107 決定3）。
        var source = new NoOpFxRateSource(NullLogger<NoOpFxRateSource>.Instance);

        var rate = await source.GetRateToBaseAsync(Currency.Jpy);

        rate!.Rate.Should().Be(1m);
    }

    private static IFxRateSource Create(FxOptions options, ILoggerFactory? loggerFactory = null) =>
        FxRateSourceFactory.Create(
            options, new HttpClient(), TimeProvider.System, loggerFactory ?? NullLoggerFactory.Instance);

    // 構成不備・クランプの警告は「有効化したつもりで効いていない／緩めたつもりで丸められた」の唯一の検知点
    // であるため、出力そのものを検証する。中央パッケージ管理にログ用の偽装は無いので最小の実装を置く。
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_warnings);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    warnings.Add(formatter(state, exception));
            }
        }
    }
}
