using System.Net;
using System.Text.Json;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Worker.Composable.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Worker.Tests;

// #79, FR-04, IADR-0017/0039: platform LLM ゲートウェイ POST /complete を呼ぶ実 LLM egress の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。送信拒否/失敗/タイムアウトは Hold（取引しない）に倒す。
// IADR-0055 決定3: 成功応答のトークンを ILlmUsageReporter へ渡す（計測は best-effort＝応答を壊さない）。
public class HttpLlmCompletionClientTests
{
    private static HttpLlmCompletionClient Client(HttpMessageHandler handler, ILlmUsageReporter? reporter = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", "trade-decision",
            reporter ?? new NoOpLlmUsageReporter());

    // 計測を記録するだけの fake（publish しない）。
    private sealed class RecordingReporter : ILlmUsageReporter
    {
        public LlmUsage? Last { get; private set; }
        public int Calls { get; private set; }

        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
        {
            Last = usage;
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingReporter : ILlmUsageReporter
    {
        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("計測の発行に失敗");
    }

    [Fact]
    public async Task 送信成功_Sent_true_は_本文を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\"}","model":"claude","inputTokens":10,"outputTokens":5,"sent":true}""");

        var text = await Client(handler).CompleteAsync("prompt", "primary");

        text.Should().Be("""{"action":"Buy"}""");
        handler.LastPath.Should().Be("/complete");
    }

    [Fact]
    public async Task 送信拒否_Sent_false_は_Hold_取引しない()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"機密区分により送信できません","model":"","inputTokens":0,"outputTokens":0,"sent":false}""");
        (await Client(handler).CompleteAsync("prompt")).Should().Contain("Hold");
    }

    [Fact]
    public async Task 非_2xx_は_Hold_取引しない()
    {
        (await Client(new StubHandler(HttpStatusCode.InternalServerError, "")).CompleteAsync("prompt"))
            .Should().Contain("Hold");
    }

    [Fact]
    public async Task 例外_不達_は_Hold_取引しない()
    {
        (await Client(new ThrowingHandler()).CompleteAsync("prompt")).Should().Contain("Hold");
    }

    [Fact]
    public async Task 空_不正ボディの_200_は_Hold_取引しない()
    {
        (await Client(new StubHandler(HttpStatusCode.OK, "")).CompleteAsync("prompt")).Should().Contain("Hold");
        (await Client(new StubHandler(HttpStatusCode.OK, "not-json")).CompleteAsync("prompt")).Should().Contain("Hold");
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は_Hold_取引しない()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://llm-gateway"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var client = new HttpLlmCompletionClient(http, NullLogger<HttpLlmCompletionClient>.Instance, "internal", "trade-decision",
            new NoOpLlmUsageReporter());
        (await client.CompleteAsync("prompt")).Should().Contain("Hold");
    }

    // #79, IADR-0055 決定3: 成功応答のトークンを費用計測へ渡す（計測点は egress）。
    [Fact]
    public async Task 送信成功時_応答トークンを費用計測へ渡す()
    {
        var reporter = new RecordingReporter();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\"}","model":"claude","inputTokens":120,"outputTokens":34,"sent":true}""");

        await Client(handler, reporter).CompleteAsync("prompt");

        reporter.Calls.Should().Be(1);
        reporter.Last.Should().Be(new LlmUsage(120, 34));
    }

    // 送信拒否・失敗時は費用が発生していないため計測しない。
    [Fact]
    public async Task 送信拒否や非2xx_では費用計測しない()
    {
        var rejected = new RecordingReporter();
        await Client(new StubHandler(HttpStatusCode.OK,
            """{"text":"拒否","model":"","inputTokens":0,"outputTokens":0,"sent":false}"""), rejected).CompleteAsync("p");
        rejected.Calls.Should().Be(0);

        var failed = new RecordingReporter();
        await Client(new StubHandler(HttpStatusCode.InternalServerError, ""), failed).CompleteAsync("p");
        failed.Calls.Should().Be(0);
    }

    // 計測は best-effort: 発行に失敗しても LLM 応答は壊さない（Hold に倒さない）。
    [Fact]
    public async Task 費用計測の失敗は_LLM応答を壊さない()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\"}","model":"claude","inputTokens":10,"outputTokens":5,"sent":true}""");

        var text = await Client(handler, new ThrowingReporter()).CompleteAsync("prompt");

        text.Should().Be("""{"action":"Buy"}""");
        text.Should().NotContain("Hold");
    }

    // 応答にトークンが無い（欠落）場合は 0 として扱う。
    [Fact]
    public async Task 応答にトークンが無い場合は_0_として計測する()
    {
        var reporter = new RecordingReporter();
        await Client(new StubHandler(HttpStatusCode.OK, """{"text":"{}","sent":true}"""), reporter).CompleteAsync("p");

        reporter.Last.Should().Be(new LlmUsage(0, 0));
    }

    // ADR-0010（platform LLM ゲートウェイの越境ルーティング）: 要求に prompt/model/confidentiality/purpose を載せる。
    [Fact]
    public async Task 要求に_prompt_model_confidentiality_purpose_を載せる()
    {
        var handler = new CapturingHandler("""{"text":"{}","model":"m","inputTokens":1,"outputTokens":1,"sent":true}""");
        await Client(handler).CompleteAsync("私のプロンプト", "primary-model");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("prompt").GetString().Should().Be("私のプロンプト");
        doc.RootElement.GetProperty("model").GetString().Should().Be("primary-model");
        doc.RootElement.GetProperty("confidentiality").GetString().Should().Be("internal");
        doc.RootElement.GetProperty("purpose").GetString().Should().Be("trade-decision");
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

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("LLM ゲートウェイ不達");
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
