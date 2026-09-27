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

    // FR-01, NFR（費用）, #915, IADR-0031: 200 OK でも項目が無い本文は、既定値（false / 0）ではなく Normal（1×）へ倒す。
    // 是正前は本文 {} が (Halted=false, IntervalMultiplier=0) として写っていた（消費側の下限 1 で隠れていただけ）。
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"isHalted":false}""")]
    [InlineData("""{"isHalted":false,"intervalMultiplier":null}""")]
    [InlineData("""{"state":"Throttled","isHalted":false}""")]
    public async Task 不正_200_OK_で_intervalMultiplier_欠落_は_Normal(string body)
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, body)).GetAsync()).Should().Be(CostControlGateNormal());
    }

    // FR-01, NFR（費用）, #915: 停止していない応答の倍率が 0・負なら 0× / 負倍で写さず Normal（1×）。
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-0.5")]
    public async Task 不正_200_OK_で_intervalMultiplier_が非正_は_Normal(string multiplier)
    {
        var gate = await Gate(new StubHandler(HttpStatusCode.OK,
            $$"""{"isHalted":false,"intervalMultiplier":{{multiplier}}}""")).GetAsync();

        gate.Should().Be(CostControlGateNormal());
    }

    // FR-01, NFR（費用）, #915: isHalted が無い応答は停止か否かを判定できない。倍率があっても写さず Normal。
    [Theory]
    [InlineData("""{"intervalMultiplier":2.0}""")]
    [InlineData("""{"isHalted":null,"intervalMultiplier":2.0}""")]
    public async Task 不正_200_OK_で_isHalted_欠落_は_Normal(string body)
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, body)).GetAsync()).Should().Be(CostControlGateNormal());
    }

    // FR-01, NFR（費用）, #915: 停止が明示されていれば、倍率が欠落・0 でも停止を尊重する（Normal へ落とさない）。
    // 送り手は Halted で倍率 0（無効値）を返すのが正常であり、倍率の検査を停止より先に当てると費用上限を無視して収集を続ける。
    [Theory]
    [InlineData("""{"isHalted":true}""")]
    [InlineData("""{"isHalted":true,"intervalMultiplier":0}""")]
    [InlineData("""{"isHalted":true,"intervalMultiplier":null}""")]
    public async Task 停止_Halted_明示は_intervalMultiplier_が欠落_0_でも停止する(string body)
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, body)).GetAsync()).Halted.Should().BeTrue();
    }

    // T-10-1712, FR-01, NFR（費用）, #1063 B: 停止の旗が読めていれば、倍率だけが読めなくても停止を守る。
    // 以前は 1 つの DTO へ一括で逆直列化していたため、倍率が読めないと本文全体が不正応答になり Normal（停止せず）へ倒れていた。
    [Theory]
    [InlineData("""{"isHalted":true,"intervalMultiplier":"two"}""")]
    [InlineData("""{"isHalted":true,"intervalMultiplier":79228162514264337593543950336}""")]
    [InlineData("""{"isHalted":true,"intervalMultiplier":{"x":1}}""")]
    [InlineData("""{"isHalted":true,"intervalMultiplier":" 2"}""")]
    public async Task T_10_1712_停止の旗が読めれば倍率が読めなくても停止を守る(string body)
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, body)).GetAsync()).Should().Be(
            new InformationCollectionService.Features.InformationCollection.CostControlGate(true, 0m));
    }

    // T-10-1712 の対（変わらないこと）: 停止していない応答の倍率が読めなければ、従来どおり Normal（1×）。
    // 項目の読み方は以前の Web 既定の逆直列化と同じ（名前の大小を区別しない・数値の文字列も読む・真偽でない isHalted は判定できない）。
    [Theory]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"two"}""", false, 1)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":79228162514264337593543950336}""", false, 1)]
    [InlineData("""{"IsHalted":false,"IntervalMultiplier":2}""", false, 2)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"2"}""", false, 2)]
    // ［2026-09-27 追記 / #1065 F2a］T-10-1722: 前後に空白のある数値の文字列は、以前の Web 既定の逆直列化と同じく拒む（Normal 1 倍）。
    [InlineData("""{"isHalted":false,"intervalMultiplier":" 2"}""", false, 1)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"2 "}""", false, 1)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"1,000"}""", false, 1)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"-2"}""", false, 1)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"2.5"}""", false, 2.5)]
    [InlineData("""{"isHalted":false,"intervalMultiplier":"2e0"}""", false, 2)]
    [InlineData("""{"isHalted":"true","intervalMultiplier":0}""", false, 1)]
    [InlineData("""[]""", false, 1)]
    [InlineData("""null""", false, 1)]
    public async Task T_10_1712_停止していない応答や読めない本文の扱いは従来どおり(string body, bool halted, double multiplier)
    {
        (await Gate(new StubHandler(HttpStatusCode.OK, body)).GetAsync()).Should().Be(
            new InformationCollectionService.Features.InformationCollection.CostControlGate(halted, (decimal)multiplier));
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
