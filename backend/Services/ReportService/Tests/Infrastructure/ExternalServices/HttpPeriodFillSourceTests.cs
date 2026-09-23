using System.Net;
using System.Text;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06/16, IADR-0095/0115 決定5, #280: 権威源（リスク管理の取引台帳）への s2s 照会と、その fail-safe を
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。供給不達はすべて空列＝数値 0 の報告書として生成を続ける。
public class HttpPeriodFillSourceTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static readonly DateOnly From = new(2026, 7, 6);
    private static readonly DateOnly To = new(2026, 7, 10);

    private static HttpPeriodFillSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpPeriodFillSource>.Instance);

    [Fact]
    public async Task 期間を要求のクエリ文字列に載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");

        await Source(handler).GetFillsAsync(From, To);

        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/fills");
        handler.LastUri.Query.Should().Contain("from=2026-07-06").And.Contain("to=2026-07-10");
    }

    [Fact]
    public async Task 台帳の約定を集計入力へ写す()
    {
        // 列挙は数値で往復する（権威源は JsonStringEnumConverter を構成していない）。
        // market=0（Japan）・side=1（Sell）・positionEffect=1（Close）。
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"7203","market":0,"side":1,"positionEffect":1,"quantity":100,
              "price":2100.5,"executedAt":"2026-07-08T01:00:00+00:00","fxRateToBase":1}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        var fill = fills.Should().ContainSingle().Subject;
        fill.Symbol.Should().Be("7203");
        fill.Market.Should().Be(Market.Japan);
        fill.Side.Should().Be(TradeSide.Sell);
        fill.PositionEffect.Should().Be(PositionEffect.Close);
        fill.Quantity.Should().Be(100);
        fill.Price.Should().Be(2100.5m);
    }

    // 🔴 **肯定形（#563, IADR-0269）**: 判断記録と突き合わせる相関キー（DecisionId）をそのまま通す。
    // これが欠けると日報 §2 の「判断根拠（要約）」が全行で未供給になる。
    [Fact]
    public async Task 台帳のDecisionIdをそのまま通す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"7203","market":0,"side":0,"positionEffect":0,"quantity":100,
              "price":2500,"executedAt":"2026-07-08T01:00:00+00:00","fxRateToBase":1,
              "decisionId":"11111111-1111-1111-1111-111111111111"}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.DecisionId
            .Should().Be(new Guid("11111111-1111-1111-1111-111111111111"));
    }

    // 🔴 **否定形（上の肯定形と対）**: 相関キーを欠く応答へ別の値を作らない（無関係な根拠が明細へ載る）。
    [Fact]
    public async Task DecisionIdを欠く応答は相関できないままにする()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"7203","market":0,"side":0,"positionEffect":0,"quantity":100,
              "price":2500,"executedAt":"2026-07-08T01:00:00+00:00","fxRateToBase":1}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.DecisionId.Should().Be(Guid.Empty);
    }

    // 🔴 **肯定形（#569, IADR-0271）**: 実際に発注したアダプタの発注先をそのまま通す。
    // これが欠けると月報 §5 の三者比較で SIMULATE 列と実弾列が分けられない。
    // provider=2（MoomooSimulate）。
    [Fact]
    public async Task 台帳の発注先をそのまま通す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"7203","market":0,"side":0,"positionEffect":0,"quantity":100,
              "price":2500,"executedAt":"2026-07-08T01:00:00+00:00","fxRateToBase":1,"provider":2}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.Provider.Should().Be(BrokerProvider.MoomooSimulate);
    }

    // 🔴 **否定形（上の肯定形と対）**: 発注先を欠く応答へ値を作らない。
    // 既定の列挙値（0＝InternalPaper）へ落ちると、**発注先不明の約定が「内蔵 paper」になる**。
    [Fact]
    public async Task 発注先を欠く応答は発注先不明のままにする()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"7203","market":0,"side":0,"positionEffect":0,"quantity":100,
              "price":2500,"executedAt":"2026-07-08T01:00:00+00:00","fxRateToBase":1}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.Provider.Should().BeNull();
    }

    [Fact]
    public async Task 外貨建ての約定単価は同伴レートで基準通貨へ換算する()
    {
        // IADR-0107: 台帳の Price はローカル通貨（USD）。報告書の集計は基準通貨（円）建てのため換算する。
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"AAPL","market":1,"side":0,"positionEffect":0,"quantity":10,
              "price":200,"executedAt":"2026-07-08T14:00:00+00:00","fxRateToBase":150}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.Price.Should().Be(30_000m);
    }

    [Fact]
    public async Task レート欠落の応答は基準通貨建てとして扱う()
    {
        // 列追加前の既存行（FxRateToBase 未記録）は 1＝基準通貨建てとみなす（台帳側の既定と同じ）。
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"7203","market":0,"side":0,"positionEffect":0,"quantity":100,
              "price":2000,"executedAt":"2026-07-08T01:00:00+00:00"}]
            """);

        (await Source(handler).GetFillsAsync(From, To)).Should().ContainSingle().Which.Price.Should().Be(2000m);
    }

    [Fact]
    public async Task 銘柄コードが空の要素は落とす()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"","market":0,"side":0,"positionEffect":0,"quantity":1,
              "price":1,"executedAt":"2026-07-08T01:00:00+00:00"}]
            """);

        (await Source(handler).GetFillsAsync(From, To)).Should().BeEmpty();
    }

    [Fact]
    public async Task 非_2xx_は空列へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.Forbidden, "")).GetFillsAsync(From, To)).Should().BeEmpty();
    }

    [Fact]
    public async Task 不正なボディは空列へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "not-json")).GetFillsAsync(From, To)).Should().BeEmpty();
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetFillsAsync(From, To)).Should().BeEmpty();
    }

    [Fact]
    public async Task 例外_不達_は空列へ倒す()
    {
        (await Source(new ThrowingHandler()).GetFillsAsync(From, To)).Should().BeEmpty();
    }

    [Fact]
    public async Task タイムアウトは空列へ倒す()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 本ケースは遅延が勝っても空列になるため赤くはならなかったが、その代わり**打ち切り経路を黙って
        // 検査しなくなる**（変異注入で実測: 遅延を勝たせても緑のままだった）。
        // 応答が返らない上流に変え、打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://risk-management"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var source = new HttpPeriodFillSource(http, NullLogger<HttpPeriodFillSource>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        (await source.GetFillsAsync(From, To).WaitAsync(Guard)).Should().BeEmpty();
        (await handler.Cancellation.WaitAsync(Guard)).Should()
            .BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
    }

    // 🔴 **肯定形（#611, IADR-0286 決定1）**: 承認時点の認識時レート（1 USD あたりの円）をそのまま通す。
    // これが欠けると為替差損益が全期間で未供給になる。
    [Fact]
    public async Task 台帳の認識時レートをそのまま通す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"AAPL","market":1,"side":0,"positionEffect":0,"quantity":10,
              "price":100,"executedAt":"2026-07-08T14:30:00+00:00","fxRateToBase":1,
              "fxRateBaseToDisplay":159.38}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.FxRateBaseToDisplay.Should().Be(159.38m);
    }

    // 🔴 **否定形（#611, IADR-0286 決定3）**: 欠落した応答（旧版 Risk・列追加前の行・未解決の行）は **null のまま**にする。
    // FxRateToBase の既定 1 と違い、既定へ倒す正当な値が無い（1 円/ドルは事実ではない）。推定で埋めない。
    [Fact]
    public async Task 認識時レートが無い応答はnullのまま通す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            [{"symbol":"AAPL","market":1,"side":0,"positionEffect":0,"quantity":10,
              "price":100,"executedAt":"2026-07-08T14:30:00+00:00","fxRateToBase":1}]
            """);

        var fills = await Source(handler).GetFillsAsync(From, To);

        fills.Should().ContainSingle().Which.FxRateBaseToDisplay.Should().BeNull();
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("接続できません");
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
