using System.Net;
using System.Text;
using ReportService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-15, FR-20, #569, IADR-0051, IADR-0271: 現在の運用段階の s2s 照会と、その fail-safe。
//
// 🔴 段階は三者比較の「空欄（その段をまだ走らせていない）」と「値 0」を分ける鍵である。
// **既定（Stage 0）へ倒すと、到達済みの段の列が静かに空欄になる。**
public class HttpStageProgressSourceTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpStageProgressSource Source(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk-management") },
            NullLogger<HttpStageProgressSource>.Instance);

    // **対の肯定形**: 供給された段階をそのまま返す。
    [Theory]
    [InlineData(0, TradingStage.Stage0Verification)]
    [InlineData(1, TradingStage.Stage1Simulate)]
    [InlineData(2, TradingStage.Stage2MinimalLive)]
    [InlineData(3, TradingStage.Stage3ScaledLive)]
    public async Task 現在段階を写す(int raw, TradingStage expected)
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"currentStage":{{raw}},"history":[]}""");

        (await Source(handler).GetCurrentStageAsync()).Should().Be(expected);
        handler.LastUri!.AbsolutePath.Should().Be("/risk-controls/stage-gate");
    }

    // 🔴 **否定形**: 供給不達はすべて null（未供給）へ倒す。Stage 0 へ倒さない。
    [Fact]
    public async Task 非_2xx_は未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.Forbidden, "")).GetCurrentStageAsync()).Should().BeNull();
    }

    [Fact]
    public async Task 不正なボディは未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, "not-json")).GetCurrentStageAsync()).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "null")).GetCurrentStageAsync()).Should().BeNull();
        (await Source(new StubHandler(HttpStatusCode.OK, "{}")).GetCurrentStageAsync()).Should().BeNull();
    }

    // 🔴 **未定義の列挙値を素通ししない。** 権威源が段階を増やしたとき、未知の値を
    // 「到達済み」として比較へ流すと、走らせていない段の列が埋まる。
    [Fact]
    public async Task 未知の段階値は未供給へ倒す()
    {
        (await Source(new StubHandler(HttpStatusCode.OK, """{"currentStage":99}""")).GetCurrentStageAsync())
            .Should().BeNull();
    }

    [Fact]
    public async Task 例外_不達_は未供給へ倒す()
    {
        (await Source(new ThrowingHandler()).GetCurrentStageAsync()).Should().BeNull();
    }

    [Fact]
    public async Task タイムアウトは未供給へ倒す()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 遅延が勝つと 200 応答が写って非 null になり**実際に赤くなる**（変異注入で実測）。
        // 応答が返らない上流に変え、打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://risk-management"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        (await new HttpStageProgressSource(http, NullLogger<HttpStageProgressSource>.Instance)
            .GetCurrentStageAsync().WaitAsync(Guard)).Should().BeNull();
        (await handler.Cancellation.WaitAsync(Guard)).Should()
            .BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
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
