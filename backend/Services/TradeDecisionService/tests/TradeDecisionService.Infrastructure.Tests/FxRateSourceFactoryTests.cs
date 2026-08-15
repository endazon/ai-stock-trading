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

    // #381, ADR-0022 決定1, IADR-0194 決定5: 日銀は**認証不要**である。
    // FRED と違い、キーが無くても no-op へ倒れない（倒れると第一の情報源が永久に使われない）。
    [Theory]
    [InlineData("boj")]
    [InlineData("Boj")]
    [InlineData(" BOJ ")]
    public void boj指定はAPIキー無しでも実レート源になる(string provider)
    {
        var source = Create(new FxOptions { Provider = provider });

        // 実レート源は TTL・鮮度判定の装飾を必ず経由する（生の取得器を直に配らない）。
        source.Should().BeOfType<CachingFxRateSource>();
    }

    // #381, ADR-0022 決定2, IADR-0194 決定4: 日銀を第一・FRED をフォールバックとする順位つき合成。
    // 合成の順序は Caching( Fallback( Boj, Fred ) ) である——鮮度判定は**採用された値**に対して効くべきであり、
    // フォールバックの内側に置くと源ごとに別々の警告が出てどちらの値が使われたのか読めなくなる。
    [Fact]
    public void boj指定でFREDのキーがあれば順位つきフォールバックを組む()
    {
        var source = Create(new FxOptions
        {
            Provider = "boj",
            Fred = new FredFxOptions { ApiKey = "key" },
        });

        source.Should().BeOfType<CachingFxRateSource>();
        Inner(source).Should().BeOfType<FallbackFxRateSource>("鮮度判定は合成の最外に置く");
    }

    // FRED のキーが無ければ日銀単独になる（フォールバックの合成を挟まない）。
    [Fact]
    public void boj指定でFREDのキーが無ければ日銀単独になる()
    {
        var source = Create(new FxOptions { Provider = "boj" });

        Inner(source).Should().BeOfType<BojFxRateSource>();
    }

    // 🔴 冗長化が無い状態は ADR-0022 決定2 が求める形ではない。**黙って単独運転にしない。**
    [Fact]
    public void boj単独運転になるときは冗長化が無いことを警告する()
    {
        var loggerFactory = new CapturingLoggerFactory();

        Create(new FxOptions { Provider = "boj" }, loggerFactory);

        loggerFactory.Warnings.Should().ContainSingle(w => w.Contains("フォールバックがありません"));
    }

    // **否定形**: フォールバックが揃っているときに「冗長化が無い」と警告しない（狼少年にしない）。
    [Fact]
    public void フォールバックが揃っていれば冗長化の警告は出さない()
    {
        var loggerFactory = new CapturingLoggerFactory();

        Create(
            new FxOptions { Provider = "boj", Fred = new FredFxOptions { ApiKey = "key" } },
            loggerFactory);

        loggerFactory.Warnings.Should().NotContain(w => w.Contains("フォールバックがありません"));
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

    // FR-10, ADR-0022, IADR-0135 決定6, #378: ProviderNames は「計画適合検査が読む集合」と
    // 「実装が実際に受け付ける集合」を一致させるための単一情報源であり、飾りのメンバではない。
    // 分岐（Create / ResolveProvider）が同メンバを関門として通ることを、両向きで確かめる。
    [Fact]
    public void 公開するprovider集合は実装が実際に受け付ける集合と一致する()
    {
        FxRateSourceFactory.ProviderNames.Should().Equal(
            FxRateSourceFactory.None, FxRateSourceFactory.Boj, FxRateSourceFactory.Fred);

        // (1) 集合の各名前は実際に分岐へ到達する（到達しなければ「飾りのメンバ」である）。
        FxRateSourceFactory.ResolveProvider(new FxOptions { Provider = FxRateSourceFactory.None })
            .Should().Be(FxRateSourceFactory.None);
        // #381, IADR-0194 決定5: 日銀は**認証不要**のため、キー無しでも分岐へ到達する（no-op へ倒れない）。
        FxRateSourceFactory.ResolveProvider(new FxOptions { Provider = FxRateSourceFactory.Boj })
            .Should().Be(FxRateSourceFactory.Boj);
        FxRateSourceFactory.ResolveProvider(Fred(days: FxOptions.DefaultMaxRateAgeDays))
            .Should().Be(FxRateSourceFactory.Fred);

        // (2) 集合に無い名前は、構成が整っていても分岐へ到達しない。
        //     ＝ 集合への追加を忘れた provider は動かないので、更新漏れが「黙って通る」形にならない。
        var outsideTheSet = new FxOptions
        {
            Provider = "ecb",
            Fred = new FredFxOptions { ApiKey = "key" },
        };
        FxRateSourceFactory.ResolveProvider(outsideTheSet).Should().Be(FxRateSourceFactory.None);
        Create(outsideTheSet).Should().BeOfType<NoOpFxRateSource>();
    }

    // FR-10, FR-17, ADR-0022 決定5, #381, IADR-0174: 既定の鮮度上限は**計画の絶対上限 30 日**である。
    //
    // **根拠が実装から計画へ移った。** 旧値 14 は DEXJPUS の公表周期（≒12.84 日）から逆算した
    // **情報源の都合**であり（IADR-0112 決定1）、計画に根拠が無かった。2026-08-04 に計画が 30 日を確定した。
    [Fact]
    public void 既定の鮮度上限は計画の絶対上限30日()
    {
        FxRateSourceFactory.ResolveMaxRateAge(new FxOptions()).Should().Be(TimeSpan.FromDays(30));
    }

    // #381, IADR-0174 決定1: 既定の**警告**しきい値は 5 日（ADR-0022 決定4・2026-08-07 改訂）。
    [Fact]
    public void 既定の警告しきい値は5日()
    {
        FxRateSourceFactory.ResolveStaleRateWarning(new FxOptions()).Should().Be(TimeSpan.FromDays(5));
    }

    // #381, IADR-0174 決定3: **警告が上限を超えると、警告が一度も出ないまま停止する**（気づく機会が消える）。
    // 上限でクランプすることを固定する。
    [Fact]
    public void 警告しきい値は鮮度上限でクランプする()
    {
        var options = new FxOptions { MaxRateAgeDays = 3, StaleRateWarningDays = 10 };

        FxRateSourceFactory.ResolveStaleRateWarning(options).Should().Be(TimeSpan.FromDays(3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ゼロ以下の警告しきい値は既定へ倒す(int days)
    {
        FxRateSourceFactory.ResolveStaleRateWarning(new FxOptions { StaleRateWarningDays = days })
            .Should().Be(TimeSpan.FromDays(FxOptions.DefaultStaleRateWarningDays));
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
    // #381, IADR-0174 決定2: クランプが 31 → 30 へ下がったため、境界の実例も 30 へ。
    [InlineData(30)]
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
    public async Task no_opは非基準通貨のレートを解決しない()
    {
        var source = new NoOpFxRateSource(NullLogger<NoOpFxRateSource>.Instance);

        (await source.GetRateToBaseAsync(Currency.Jpy)).Should().BeNull();
    }

    [Fact]
    public async Task no_opでも基準通貨はレート1で解決する()
    {
        // FX 未有効化の環境でも基準通貨（米国株）は従来どおり取引できる＝影響を非基準通貨に限定する（IADR-0107 決定3）。
        var source = new NoOpFxRateSource(NullLogger<NoOpFxRateSource>.Instance);

        var rate = await source.GetRateToBaseAsync(Currency.Usd);

        rate!.Rate.Should().Be(1m);
    }

    private static IFxRateSource Create(FxOptions options, ILoggerFactory? loggerFactory = null) =>
        FxRateSourceFactory.Create(
            options, new HttpClient(), TimeProvider.System, loggerFactory ?? NullLoggerFactory.Instance);

    /// <summary>
    /// <see cref="CachingFxRateSource"/> が包んでいる内側の源を取り出す（合成の順序を検査するため）。
    /// 内部実装へ踏み込むのは、**合成の順序そのものが決定**（IADR-0194 決定4）だからである——
    /// 外から観察できる振る舞いだけでは「鮮度判定が最外にあるか」を区別できない。
    /// </summary>
    private static object Inner(IFxRateSource source) =>
        typeof(CachingFxRateSource)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(f => f.GetValue(source))
            .OfType<IFxRateSource>()
            .Single();

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
