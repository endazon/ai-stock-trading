using System.Net;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InformationCollectionService.Tests;

// NFR（費用）, IADR-0031: 費用統制の GET /costs/state を同期照会する実装の写像とフェイルセーフを fake HttpMessageHandler で
// 検証する（実ネットワーク不使用）。未取得系は Normal（停止せず・1×）へ倒す。
public class HttpCostControlGateTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpCostControlGate Gate(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://cost") },
            NullLogger<HttpCostControlGate>.Instance);

    [Fact]
    public async Task 間隔延長_Throttled_応答を写像する()
    {
        // CostControlDecision の JSON（isHalted/intervalMultiplier を疎結合に読む）。
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"state":1,"intervalMultiplier":2.0,"isHalted":false}""");

        var gate = await Gate(handler).GetAsync();

        gate.Halted.Should().BeFalse();
        gate.IntervalMultiplier.Should().Be(2.0m);
        handler.LastPath.Should().Be("/costs/state");
    }

    [Fact]
    public async Task 停止_Halted_応答を写像する()
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.OK,
            """{"state":2,"intervalMultiplier":0,"isHalted":true}""")).GetAsync();

        gate.Halted.Should().BeTrue();
    }

    [Fact]
    public async Task 未取得_404_は_Normal_停止せず()
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.NotFound, "")).GetAsync();
        gate.Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task 非_2xx_は_Normal_停止せず()
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.Unauthorized, "")).GetAsync();
        gate.Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task 例外_不達_は_Normal_停止せず()
    {
        (await Gate(new ThrowingHandler()).GetAsync()).Should().Be(CostControlGateNormal());
    }

    [Fact]
    public async Task 不正_空ボディの_200_は_Normal_停止せず()
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, "")).GetAsync()).Should().Be(CostControlGateNormal());
    }

    // NFR（費用）, IADR-0031: 費用統制の応答が上限に間に合わなければ、情報収集は止めず Normal（1×）へ倒す。
    //
    // #901, IADR-0367: 従来は「壁時計 50 ms の `HttpClient.Timeout`」対「壁時計 2 秒のハンドラ遅延」という
    // **時刻どうしの競争**で合否が決まっていた。全ソリューション実行では稀に遅延が勝ち、ハンドラの本文 `{}` が
    // そのまま写って `IntervalMultiplier = 0`（＝Normal ではない）になって落ちた（機序は IADR-0367）。
    // 遅延をやめ、**打ち切られるまで決して応答しない**ハンドラにする。上流が上限より遅いことは変わらず、
    // 「遅い上流でも Normal へ倒す」という固定したい性質は同じで、応答が勝つ余地だけが消える。
    [Fact]
    public async Task タイムアウト_応答遅延_は_Normal_停止せず()
    {
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://cost"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var gate = new HttpCostControlGate(http, NullLogger<HttpCostControlGate>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        (await gate.GetAsync().WaitAsync(Guard)).Should().Be(CostControlGateNormal());
        // 応答ではなく**打ち切り**で終わったことを、ハンドラ側の観測で確定させる。
        (await handler.Cancellation.WaitAsync(Guard)).Should().BeTrue("上限に達した要求は打ち切られる（応答は返っていない）");
    }

    private static InformationCollectionService.Features.InformationCollection.CostControlGate CostControlGateNormal() =>
        InformationCollectionService.Features.InformationCollection.CostControlGate.Normal;

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
            throw new HttpRequestException("費用統制サービス不達");
    }

    // #901, IADR-0367: 時間では応答しない上流。終わり方は打ち切り（＝要求トークンの発火）だけで、
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
