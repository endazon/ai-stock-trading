using System.Net;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-07, IADR-0028: 報告書サービスの GET /reports/daily-policy を同期照会する実装の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。
public class HttpDailyPolicyProviderTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpDailyPolicyProvider Provider(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://reports") },
            NullLogger<HttpDailyPolicyProvider>.Instance);

    [Fact]
    public async Task 応答を_DailyPolicy_に写像する()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"date":"2026-07-10","summary":"米国株の押し目買い","assumptionsVersion":1}""");

        var policy = await Provider(handler).GetCurrentAsync();

        policy.Should().NotBeNull();
        policy!.Date.Should().Be(new DateOnly(2026, 7, 10));
        policy.Summary.Should().Be("米国株の押し目買い");
        handler.LastPath.Should().Be("/reports/daily-policy");
    }

    [Fact]
    public async Task 未確定_404_は_null_取引しない()
    {
        var policy = await Provider(new StubHandler(HttpStatusCode.NotFound, "")).GetCurrentAsync();
        policy.Should().BeNull();
    }

    [Fact]
    public async Task 非_2xx_は_null_取引しない()
    {
        var policy = await Provider(new StubHandler(HttpStatusCode.Unauthorized, "")).GetCurrentAsync();
        policy.Should().BeNull();
    }

    [Fact]
    public async Task 例外_不達_は_null_取引しない()
    {
        var policy = await Provider(new ThrowingHandler()).GetCurrentAsync();
        policy.Should().BeNull();
    }

    [Fact]
    public async Task 不正_空ボディの_200_は_null_取引しない()
    {
        (await Provider(new StubHandler(HttpStatusCode.OK, "")).GetCurrentAsync()).Should().BeNull();
        (await Provider(new StubHandler(HttpStatusCode.OK, "not-json")).GetCurrentAsync()).Should().BeNull();
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は_null_取引しない()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 遅延が勝つと 200 応答（本文 `{}`）が写って非 null になり**実際に赤くなる**（変異注入で実測）。
        // 応答が返らない上流に変え、打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://reports"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new HttpDailyPolicyProvider(http, NullLogger<HttpDailyPolicyProvider>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        (await provider.GetCurrentAsync().WaitAsync(Guard)).Should().BeNull();
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
            throw new HttpRequestException("報告書サービス不達");
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
