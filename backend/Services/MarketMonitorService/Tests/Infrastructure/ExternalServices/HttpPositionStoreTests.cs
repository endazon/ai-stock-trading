using System.Net;
using System.Text.Json;
using MarketMonitorService.Domain;
using MarketMonitorService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-10, IADR-0030: リスク管理の GET /risk-controls/open-positions を同期照会する実装の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。未取得系は空列（＝損切り検知対象なし）へ倒す。
public class HttpPositionStoreTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpPositionStore Store(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpPositionStore>.Instance);

    [Fact]
    public async Task 応答を_HeldPosition_列に写像する()
    {
        // web 既定（camelCase・列挙は数値）で往復させる JSON を用意する。
        var payload = new[]
        {
            new HeldPosition("AAPL", Market.UnitedStates, TradeSide.Buy, 10, 1_000m, 970m),
            new HeldPosition("MSFT", Market.UnitedStates, TradeSide.Sell, 5, 2_000m, 2_060m),
        };
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var positions = await Store(handler).GetOpenPositionsAsync();

        positions.Should().HaveCount(2);
        var aapl = positions.Single(p => p.Symbol == "AAPL");
        aapl.Side.Should().Be(TradeSide.Buy);
        aapl.Quantity.Should().Be(10);
        aapl.StopLossPrice.Should().Be(970m);
        handler.LastPath.Should().Be("/risk-controls/open-positions");
    }

    [Fact]
    public async Task 未取得_404_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.NotFound, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 非_2xx_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.Unauthorized, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 例外_不達_は空列_損切り検知対象なし()
    {
        (await Store(new ThrowingHandler()).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task 不正_空ボディの_200_は空列_損切り検知対象なし()
    {
        (await Store(new StubHandler(HttpStatusCode.OK, "")).GetOpenPositionsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は空列_損切り検知対象なし()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 本ケースは遅延が勝っても空列になるため赤くはならなかったが、その代わり**打ち切り経路を黙って
        // 検査しなくなる**（変異注入で実測: 遅延を勝たせても緑のままだった）。応答が返らない上流に変え、
        // 打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://risk"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var store = new HttpPositionStore(http, NullLogger<HttpPositionStore>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        (await store.GetOpenPositionsAsync().WaitAsync(Guard)).Should().BeEmpty();
        (await handler.Cancellation.WaitAsync(Guard)).Should()
            .BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
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
            throw new HttpRequestException("リスク管理サービス不達");
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
