using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-02, FR-13, UC-06, SC-02, IADR-0088/0095: 権威源（市場監視 #10）の GET /monitor/watchlist を s2s 同期照会する実装の
// 写像とフェイルセーフを fake HttpMessageHandler で検証する（実ネットワーク不使用）。
// ［2026-10-01 改訂 / #1134, IADR-0475］供給不達（非 2xx・timeout・例外・不正応答）は構成の既定 watchlist へ倒さない。
// 一度も読めていなければ null（不明）、読めた後は直前に読めた一覧（直前値の振る舞いは WatchlistLastKnownTests）。
// 以前の本試験は「既定 watchlist へフォールバックする」を表明していた（旧挙動）。
public class HttpWatchlistProviderTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpWatchlistProvider Provider(HttpMessageHandler handler, WatchlistLastKnown? lastKnown = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://monitor") },
            lastKnown ?? NewLastKnown(),
            NullLogger<HttpWatchlistProvider>.Instance);

    private static WatchlistLastKnown NewLastKnown() => new(NullLogger<WatchlistLastKnown>.Instance);

    [Fact]
    public async Task 権威源の_watchlist_を_WatchedSymbol_に写像する()
    {
        // MonitoredSymbol（MarketMonitorService.Domain）と WatchedSymbol は同形。web 既定（camelCase・列挙は数値）で往復する。
        var payload = new[] { new WatchedSymbol("7203", Market.Japan), new WatchedSymbol("AAPL", Market.UnitedStates) };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var result = await Provider(handler).GetWatchlistAsync();

        result.Should().HaveCount(2);
        result.Should().ContainEquivalentOf(new WatchedSymbol("7203", Market.Japan));
        result.Should().ContainEquivalentOf(new WatchedSymbol("AAPL", Market.UnitedStates));
        handler.LastPath.Should().Be("/monitor/watchlist");
    }

    [Fact]
    public async Task 空の_watchlist_をそのまま返す_フォールバックしない()
    {
        // 権威源が空（利用者が全削除）なら、その事実（何も判断しない）を尊重し fallback へ倒さない。
        var handler = new StubHandler(HttpStatusCode.OK, "[]");

        var result = await Provider(handler).GetWatchlistAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task 応答の_空_symbol_は除外する()
    {
        var body = """[{"symbol":"7203","market":0},{"symbol":"  ","market":0}]""";
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var result = await Provider(handler).GetWatchlistAsync();

        result.Should().ContainSingle().Which.Symbol.Should().Be("7203");
    }

    // T-10-1990, FR-02, ADR-0044, #1134, IADR-0475: 一度も読めていない供給不達は不明（null）。構成の既定 watchlist へは倒さない
    // （是正前は 4 本とも構成の一覧を返すことを表明していた）。
    [Theory]
    [InlineData("404")]
    [InlineData("403")]
    [InlineData("503")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("throw")]
    public async Task T_10_1990_一度も読めていない供給不達は不明を返し既定_watchlist_へ倒さない_否定形(string failure)
    {
        HttpMessageHandler handler = failure switch
        {
            "404" => new StubHandler(HttpStatusCode.NotFound, ""),
            "403" => new StubHandler(HttpStatusCode.Forbidden, ""),
            "503" => new StubHandler(HttpStatusCode.ServiceUnavailable, ""),
            "null" => new StubHandler(HttpStatusCode.OK, "null"),
            "[null]" => new StubHandler(HttpStatusCode.OK, "[null]"),
            _ => new ThrowingHandler(),
        };

        var result = await Provider(handler).GetWatchlistAsync();

        result.Should().BeNull("読めないときは不明であり、構成の既定 watchlist で判断しない");
    }

    [Fact]
    public async Task T_10_1990_タイムアウト_応答遅延_は一度も読めていなければ不明()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 遅延が勝つと 200 応答（本文 `[]`）が採られて不明にならず**実際に赤くなる**（変異注入で実測）。
        // 応答が返らない上流に変え、打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://monitor"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new HttpWatchlistProvider(http, NewLastKnown(), NullLogger<HttpWatchlistProvider>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        var result = await provider.GetWatchlistAsync().WaitAsync(Guard);

        result.Should().BeNull();
        (await handler.Cancellation.WaitAsync(Guard)).Should()
            .BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
    }

    // T-10-1545, FR-04, #1034, IADR-0440 決定 2: 判断のプロンプト用の口は、読めた一覧（空を含む）だけを返し、
    // 供給不達は構成の既定へ倒さず null（不明）で返す。🔴 既定 watchlist を「判断時点の監視銘柄」として渡さない。
    [Fact]
    public async Task 判断のプロンプト用の口は読めた一覧をそのまま返し既定へ倒さない()
    {
        var body = JsonSerializer.Serialize(
            new[] { new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("META", Market.UnitedStates) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var read = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetAuthoritativeWatchlistAsync();

        read.Should().Equal(new WatchedSymbol("AAPL", Market.UnitedStates), new WatchedSymbol("META", Market.UnitedStates));
    }

    [Fact]
    public async Task 判断のプロンプト用の口は空の一覧を空として返す_不明にしない()
    {
        var read = await Provider(new StubHandler(HttpStatusCode.OK, "[]"))
            .GetAuthoritativeWatchlistAsync();

        read.Should().NotBeNull("読めて 0 件は事実であり、不明ではない");
        read.Should().BeEmpty();
    }

    [Theory]
    [InlineData("404")]
    [InlineData("403")]
    [InlineData("500")]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task 判断のプロンプト用の口は供給不達なら不明を返し既定へ倒さない_否定形(string failure)
    {
        HttpMessageHandler handler = failure switch
        {
            "404" => new StubHandler(HttpStatusCode.NotFound, ""),
            "403" => new StubHandler(HttpStatusCode.Forbidden, ""),
            "500" => new StubHandler(HttpStatusCode.InternalServerError, ""),
            "null" => new StubHandler(HttpStatusCode.OK, "null"),
            _ => new ThrowingHandler(),
        };
        var read = await Provider(handler).GetAuthoritativeWatchlistAsync();

        read.Should().BeNull("読めないときは不明であり、構成の既定 watchlist を代わりに返さない");
    }

    // T-10-1545（PR #1041 の監査 F1）: 🔴 200 でも**1 行でも欠けていれば一覧ごと不明**。空の行を黙って落とすと 0 件・一部欠落の一覧に、
    // 欠けた市場を既定値で読むと日本株に、値域外の市場を通すと存在しない市場になり、プロンプトが「対象外」と事実でないことを書く。
    [Theory]
    [InlineData("""[{"symbol":null,"market":1}]""")]
    [InlineData("""[{"symbol":"  ","market":1}]""")]
    [InlineData("""[{"market":1}]""")]
    [InlineData("""[{"symbol":"AAPL"}]""")]
    [InlineData("""[{"symbol":"AAPL","market":null}]""")]
    [InlineData("""[{"symbol":"AAPL","market":99}]""")]
    [InlineData("""[{"symbol":"AAPL","market":-1}]""")]
    [InlineData("""[null]""")]
    [InlineData("""[{"symbol":"AAPL","market":1},{"symbol":null,"market":1}]""")]
    [InlineData("""[{"symbol":"AAPL","market":1},{"symbol":"META"}]""")]
    public async Task 判断のプロンプト用の口は欠けた行を含む応答を不明にする_否定形(string body)
    {
        var read = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetAuthoritativeWatchlistAsync();

        read.Should().BeNull("欠けた行を落として残りを一覧として見せない（不明と書く）");
    }

    // T-10-1545: 同じ応答でも定時サイクル用の口は寛容に読む —— 識別できない行を落とし、読めた行で判断を続ける（一覧ごと捨てない）。
    // ［2026-09-27 改訂 / #1063 A］T-10-1710: 市場の欠けた行・値域外の市場の行も**落とす**。以前は欠けた市場を日本（列挙の既定値）と読み、
    // 値域外の番号はそのまま通していた（本試験は `[{"symbol":"AAPL"}]` を日本として読むことを表明していた）。
    [Theory]
    [InlineData("""[{"symbol":null,"market":1},{"symbol":"MSFT","market":1}]""")]
    [InlineData("""[{"symbol":"AAPL"},{"symbol":"MSFT","market":1}]""")]
    [InlineData("""[{"symbol":"AAPL","market":null},{"symbol":"MSFT","market":1}]""")]
    [InlineData("""[{"symbol":"AAPL","market":99},{"symbol":"MSFT","market":1}]""")]
    public async Task 定時サイクル用の口は識別できない行を落とし読めた行で判断を続ける(string body)
    {
        var read = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetWatchlistAsync();

        read.Should().Equal(new WatchedSymbol("MSFT", Market.UnitedStates));
    }

    // T-10-1710: 🔴 市場の欠けた行を日本として判断対象に入れない（米国の銘柄が日本の銘柄に化ける）。全行が欠けていれば判断対象は空。
    [Fact]
    public async Task T_10_1710_定時サイクル用の口は市場の欠けた行を日本として読まない()
    {
        var read = await Provider(new StubHandler(HttpStatusCode.OK, """[{"symbol":"AAPL"}]"""))
            .GetWatchlistAsync();

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task 判断のプロンプト用の口は応答しない上流を打ち切り不明を返す()
    {
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://monitor"), Timeout = TimeSpan.FromMilliseconds(50) };
        var provider = new HttpWatchlistProvider(http, NewLastKnown(), NullLogger<HttpWatchlistProvider>.Instance);

        var read = await provider.GetAuthoritativeWatchlistAsync().WaitAsync(Guard);

        read.Should().BeNull();
        (await handler.Cancellation.WaitAsync(Guard)).Should().BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
    }

    [Fact]
    public async Task 構成ベースの供給は判断のプロンプト用の口では常に不明を返す_否定形()
    {
        // 構成に銘柄があっても、それは権威源ではない（未結線の後方互換）。
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TradeCycle:Watchlist:0:Symbol"] = "AAPL",
                ["TradeCycle:Watchlist:0:Market"] = "UnitedStates",
            })
            .Build();
        var provider = new ConfigurationWatchlistProvider(configuration);

        (await provider.GetWatchlistAsync()).Should().ContainSingle("未結線の定時サイクルは従来どおり構成を返す");
        (await provider.GetAuthoritativeWatchlistAsync()).Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("市場監視サービス不達");
    }

    // #885, IADR-0379: 時間では応答しない上流。終わり方は打ち切り（＝要求トークンの発火）だけであり、
    // 「遅延が上限に勝つ」という競争そのものが存在しない。
    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 要求が打ち切られたか（true＝上限で切られた）。テストはこれで「応答で終わっていない」ことを確定させる。
        public Task<bool> Cancellation => _cancellation.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cancellation.TrySetResult(cancellationToken.IsCancellationRequested);
            }

            throw new InvalidOperationException("到達しない（無期限待ちは打ち切りでしか終わらない）。");
        }
    }
}
