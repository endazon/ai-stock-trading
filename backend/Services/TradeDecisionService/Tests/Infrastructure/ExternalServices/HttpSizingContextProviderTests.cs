extern alias RiskManagementWorker;

using System.Net;
using System.Text.Json;
using RiskManagementWorker::RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-10, IADR-0029: リスク管理の GET /risk-controls/sizing-context を同期照会する実装の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。未取得系は残枠 0 の安全既定（取引しない）へ倒す。
public class HttpSizingContextProviderTests
{
    // 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限（合否の基準ではない）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static HttpSizingContextProvider Provider(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://risk") },
            NullLogger<HttpSizingContextProvider>.Instance);

    [Fact]
    public async Task 応答を_SizingContext_に写像する()
    {
        // web 既定（camelCase・列挙は数値）で往復させる JSON を用意する。
        var view = new SizingContext(120_000m, 45_000m, 22_000m, 2, 0.03m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());
        var body = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHandler(HttpStatusCode.OK, body);

        var context = await Provider(handler).GetContextAsync();

        context.Capital.Should().Be(120_000m);
        context.StageCapitalRemaining.Should().Be(45_000m);
        context.DailyOrderRemaining.Should().Be(22_000m);
        context.ConsecutiveLosses.Should().Be(2);
        context.DrawdownRatio.Should().Be(0.03m);
        context.Mode.Should().Be(BrokerProvider.InternalPaper);
        handler.LastPath.Should().Be("/risk-controls/sizing-context");
    }

    // FR-04, FR-10, ADR-0040 決定1, #854, IADR-0351 決定1: 損切りの実行機構の設定（判断プロンプトの「保護の状態」の供給元）。
    [Fact]
    public async Task 応答の損切りの実行機構を写像する()
    {
        // リスク管理の SizingContextView は列挙を数値で返す（2 = S2 逆指値なし）。
        var view = new SizingContext(
            120_000m, 45_000m, 22_000m, 2, 0.03m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits(),
            StopLossExecutionMethod.NoProtectiveStop);
        var body = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        body.Should().Contain("\"stopLossMethod\":2");

        var context = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetContextAsync();

        context.StopLossMethod.Should().Be(StopLossExecutionMethod.NoProtectiveStop);
    }

    // 🔴 項目を持たない旧応答・照会失敗は **null（不明）**であり S0（ブローカー側逆指値）と読まない。
    // S0 と読むと、無保護の建玉を「保護あり」と LLM へ伝え得る。
    [Fact]
    public async Task 損切りの実行機構が無い応答と照会失敗は不明でありS0と読まない()
    {
        var legacy = new SizingContext(
            120_000m, 45_000m, 22_000m, 2, 0.03m, BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());
        var body = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Replace(",\"stopLossMethod\":null", string.Empty, StringComparison.Ordinal);
        body.Should().NotContain("stopLossMethod");

        var fromLegacy = await Provider(new StubHandler(HttpStatusCode.OK, body)).GetContextAsync();
        var fromFailure = await Provider(new StubHandler(HttpStatusCode.InternalServerError, "")).GetContextAsync();

        fromLegacy.Capital.Should().Be(120_000m);
        fromLegacy.StopLossMethod.Should().BeNull();
        fromFailure.StopLossMethod.Should().BeNull();
    }

    [Fact]
    public async Task 未取得_404_は残枠0の安全既定_取引しない()
    {
        var context = await Provider(new StubHandler(HttpStatusCode.NotFound, "")).GetContextAsync();

        context.StageCapitalRemaining.Should().Be(0m);
        context.DailyOrderRemaining.Should().Be(0m);
    }

    [Fact]
    public async Task 非_2xx_は残枠0の安全既定_取引しない()
    {
        var context = await Provider(new StubHandler(HttpStatusCode.Unauthorized, "")).GetContextAsync();

        context.StageCapitalRemaining.Should().Be(0m);
        context.DailyOrderRemaining.Should().Be(0m);
    }

    [Fact]
    public async Task 例外_不達_は残枠0の安全既定_取引しない()
    {
        var context = await Provider(new ThrowingHandler()).GetContextAsync();

        context.StageCapitalRemaining.Should().Be(0m);
        context.DailyOrderRemaining.Should().Be(0m);
    }

    [Fact]
    public async Task 不正_空ボディの_200_は残枠0の安全既定_取引しない()
    {
        var context = await Provider(new StubHandler(HttpStatusCode.OK, "")).GetContextAsync();

        context.StageCapitalRemaining.Should().Be(0m);
        context.DailyOrderRemaining.Should().Be(0m);
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は残枠0の安全既定_取引しない()
    {
        // #885, IADR-0379: 従来は「壁時計 50 ms の HttpClient.Timeout」対「壁時計 2 秒のハンドラ遅延」という
        // **時刻どうしの競争**で合否が決まっていた（#900 / #901 と同型。機序は IADR-0367）。
        // 🔴 遅延が勝つと 200 応答（本文 `{}`）が写って残枠が null になり**実際に赤くなる**（変異注入で実測）。
        // 応答が返らない上流に変え、打ち切りで終わったことを観測して確定させる。**上限値（50 ms）は動かしていない。**
        var handler = new NeverRespondingHandler();
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://risk"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var provider = new HttpSizingContextProvider(http, NullLogger<HttpSizingContextProvider>.Instance);

        // Guard は「打ち切りが効かない」ときに黙って固まらないための上限であり、合否の基準ではない。
        var context = await provider.GetContextAsync().WaitAsync(Guard);

        context.StageCapitalRemaining.Should().Be(0m);
        context.DailyOrderRemaining.Should().Be(0m);
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
