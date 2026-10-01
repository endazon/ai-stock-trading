using System.Net;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using Xunit;

namespace TradeDecisionService.Tests;

// T-10-1991〜T-10-1995, T-10-1999, FR-02, FR-13, ADR-0044, #1134, IADR-0475: 定時サイクルの監視銘柄は、権威源（市場監視）が読めないとき
// 構成の既定 watchlist へ倒さず、**このプロセスで直前に読めた一覧**を使い、一度も読めていなければ不明（null）を返す。
// 本物の供給口（HttpWatchlistProvider）に、応答を途中で切り替えられる偽の上流をつないで確かめる（実ネットワーク不使用）。
// 窓（規則 11）の形とプローブの対応は作業仕様書 `20261001_1134_watchlist-no-fallback` の表。
public class WatchlistLastKnownTests
{
    private const string AaplAndToyota = """[{"symbol":"AAPL","market":1},{"symbol":"7203","market":0}]""";
    private const string MetaOnly = """[{"symbol":"META","market":1}]""";

    private static readonly WatchedSymbol Aapl = new("AAPL", Market.UnitedStates);
    private static readonly WatchedSymbol Toyota = new("7203", Market.Japan);
    private static readonly WatchedSymbol Meta = new("META", Market.UnitedStates);

    private static (HttpWatchlistProvider Provider, SwitchableHandler Upstream) Create(WatchlistLastKnown lastKnown)
    {
        var upstream = new SwitchableHandler();
        var provider = new HttpWatchlistProvider(
            new HttpClient(upstream) { BaseAddress = new Uri("http://monitor") },
            lastKnown,
            NullLogger<HttpWatchlistProvider>.Instance);
        return (provider, upstream);
    }

    private static WatchlistLastKnown NewLastKnown(ILogger<WatchlistLastKnown>? logger = null) =>
        new(logger ?? NullLogger<WatchlistLastKnown>.Instance);

    // T-10-1991: 読めたら読めた一覧（定時サイクルの寛容な読みの結果）を返し、直前の値として覚える。
    [Fact]
    public async Task T_10_1991_読めた一覧を返し直前の値として覚える()
    {
        var (provider, upstream) = Create(NewLastKnown());
        upstream.Respond(HttpStatusCode.OK, """[{"symbol":"AAPL","market":1},{"symbol":"  ","market":1},{"symbol":"7203","market":0}]""");

        (await provider.GetWatchlistAsync()).Should().Equal(Aapl, Toyota);

        upstream.Fail();
        (await provider.GetWatchlistAsync()).Should().Equal([Aapl, Toyota], "覚えたのは寛容に読んだ結果（空の行を落とした一覧）");
    }

    // T-10-1992: 🔴 読めた後に読めなければ、直前に読めた一覧で判断を続ける（全銘柄の 1 サイクル欠落を作らない・構成へ切り替えない）。
    [Theory]
    [InlineData("503")]
    [InlineData("403")]
    [InlineData("throw")]
    [InlineData("null")]
    public async Task T_10_1992_読めた後に読めなければ直前に読めた一覧を返す(string failure)
    {
        var (provider, upstream) = Create(NewLastKnown());
        upstream.Respond(HttpStatusCode.OK, AaplAndToyota);
        (await provider.GetWatchlistAsync()).Should().Equal(Aapl, Toyota);

        switch (failure)
        {
            case "503": upstream.Respond(HttpStatusCode.ServiceUnavailable, ""); break;
            case "403": upstream.Respond(HttpStatusCode.Forbidden, ""); break;
            case "null": upstream.Respond(HttpStatusCode.OK, "null"); break;
            default: upstream.Fail(); break;
        }

        (await provider.GetWatchlistAsync()).Should().Equal(Aapl, Toyota);
        (await provider.GetWatchlistAsync()).Should().Equal([Aapl, Toyota], "障害が続く間も直前の一覧のまま");
    }

    // T-10-1993: 回復すると新しい一覧を返し、以後の直前の値も新しい一覧になる（初回の値に固定しない）。
    [Fact]
    public async Task T_10_1993_回復すると新しい一覧で再開し直前の値も新しい一覧になる()
    {
        var (provider, upstream) = Create(NewLastKnown());
        upstream.Respond(HttpStatusCode.OK, AaplAndToyota);
        (await provider.GetWatchlistAsync()).Should().Equal(Aapl, Toyota);
        upstream.Fail();
        (await provider.GetWatchlistAsync()).Should().Equal(Aapl, Toyota);

        upstream.Respond(HttpStatusCode.OK, MetaOnly);
        (await provider.GetWatchlistAsync()).Should().Equal(Meta);

        upstream.Fail();
        (await provider.GetWatchlistAsync()).Should().Equal([Meta], "直前の値は最後に読めた一覧");
    }

    // T-10-1994: 読めて 0 件は事実。その後に読めなくても 0 件のまま（不明にも構成にもしない）。
    [Fact]
    public async Task T_10_1994_読めて0件の後に読めなければ0件のまま_否定形()
    {
        var (provider, upstream) = Create(NewLastKnown());
        upstream.Respond(HttpStatusCode.OK, "[]");
        (await provider.GetWatchlistAsync()).Should().BeEmpty();

        upstream.Fail();
        var read = await provider.GetWatchlistAsync();

        read.Should().NotBeNull("0 件を読めた後は不明ではない");
        read.Should().BeEmpty();
    }

    // T-10-1995: 警告は障害ごとに 1 回（連続の失敗で積もらない）。回復で Information 1 回。次の障害で再び警告。
    // 一度も読めていない障害の警告は「見送る」、読めた後の障害の警告は「直前の一覧で続ける」と書き分け、どちらも構成へ倒すとは書かない。
    [Fact]
    public async Task T_10_1995_警告は障害ごとに1回_回復は情報1回()
    {
        var logger = new CapturingLogger();
        var (provider, upstream) = Create(NewLastKnown(logger));

        upstream.Fail();
        for (var i = 0; i < 3; i++)
            (await provider.GetWatchlistAsync()).Should().BeNull();

        logger.At(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("見送ります");

        upstream.Respond(HttpStatusCode.OK, AaplAndToyota);
        await provider.GetWatchlistAsync();
        await provider.GetWatchlistAsync();
        logger.At(LogLevel.Information).Should().ContainSingle().Which.Should().Contain("再び読めました");

        upstream.Fail();
        for (var i = 0; i < 3; i++)
            await provider.GetWatchlistAsync();

        var warnings = logger.At(LogLevel.Warning);
        warnings.Should().HaveCount(2);
        warnings[1].Should().Contain("直前に読めた 2 件").And.Contain("AAPL,7203");
        warnings.Should().OnlyContain(m => m.Contains("構成の既定 watchlist へは倒しません"));
    }

    // T-10-1999: 🔴 判断のプロンプト用の口は直前の一覧を使わない（読めた後に読めなくなっても不明）。
    // 直前の一覧は「判断時点の監視銘柄」ではないため、プロンプトへは載せない（ADR-0044 決定 2）。
    [Fact]
    public async Task T_10_1999_プロンプトの口は直前の一覧を使わず不明を返す_否定形()
    {
        var (provider, upstream) = Create(NewLastKnown());
        upstream.Respond(HttpStatusCode.OK, AaplAndToyota);
        (await provider.GetWatchlistAsync()).Should().Equal(Aapl, Toyota);
        (await provider.GetAuthoritativeWatchlistAsync()).Should().Equal(Aapl, Toyota);

        upstream.Fail();

        (await provider.GetAuthoritativeWatchlistAsync()).Should().BeNull();
        (await provider.GetWatchlistAsync()).Should().Equal([Aapl, Toyota], "定時サイクルの口だけが直前の一覧を使う");
    }

    // 応答を途中で切り替えられる偽の上流。
    private sealed class SwitchableHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage> _respond = () => throw new HttpRequestException("未設定");

        public void Respond(HttpStatusCode status, string body) =>
            _respond = () => new HttpResponseMessage(status) { Content = new StringContent(body) };

        public void Fail() => _respond = () => throw new HttpRequestException("市場監視サービス不達");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond());
    }

    private sealed class CapturingLogger : ILogger<WatchlistLastKnown>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> At(LogLevel level)
        {
            lock (_entries)
                return [.. _entries.Where(e => e.Level == level).Select(e => e.Message)];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
