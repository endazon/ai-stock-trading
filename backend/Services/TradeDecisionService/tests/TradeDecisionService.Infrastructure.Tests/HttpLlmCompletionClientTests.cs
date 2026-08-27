using System.Net;
using System.Text.Json;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Domain;
using AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.TradeDecision.Infrastructure.Tests;

// #79, FR-04, IADR-0017/0039: platform LLM ゲートウェイ POST /complete を呼ぶ実 LLM egress の写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。送信拒否/失敗/タイムアウトは Hold（取引しない）に倒す。
// IADR-0055 決定3: 成功応答のトークンを ILlmUsageReporter へ渡す（計測は best-effort＝応答を壊さない）。
public class HttpLlmCompletionClientTests
{
    private static HttpLlmCompletionClient Client(HttpMessageHandler handler, ILlmUsageReporter? reporter = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            NullLogger<HttpLlmCompletionClient>.Instance, "internal", "trade-decision",
            reporter ?? new NoOpLlmUsageReporter());

    // #11, FR-11, IADR-0061 決定1: 全量ログ（プロンプト・生出力）を検証するためのクライアント。
    private static HttpLlmCompletionClient LoggingClient(HttpMessageHandler handler, ILogger<HttpLlmCompletionClient> logger, bool logPrompts) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            logger, "internal", "trade-decision", new NoOpLlmUsageReporter(), logPrompts);

    // 出力されたログ本文を記録するだけの fake。
    private sealed class RecordingLogger : ILogger<HttpLlmCompletionClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

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
            """{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","inputTokens":10,"outputTokens":5,"sent":true}""");

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
    // #303, IADR-0122 決定1: 応答が名乗った実効モデルも併せて渡す（単価解決の唯一の根拠）。
    [Fact]
    public async Task 送信成功時_応答トークンと実効モデルを費用計測へ渡す()
    {
        var reporter = new RecordingReporter();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","inputTokens":120,"outputTokens":34,"sent":true}""");

        await Client(handler, reporter).CompleteAsync("prompt");

        reporter.Calls.Should().Be(1);
        reporter.Last.Should().Be(new LlmUsage(120, 34, "claude-sonnet-5"));
    }

    // #303, IADR-0122 決定1: 要求の model は希望値でしかなく、越境ルーティング（ADR-0010）で別モデルへ着地し得る。
    // 計測へ渡すのは**応答の報告値**であること（希望値で単価を引くと恒久的にずれる）。
    [Fact]
    public async Task 要求と異なるモデルで応答しても実効モデルを計測へ渡す()
    {
        var reporter = new RecordingReporter();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Hold\"}","model":"claude-fable-5","inputTokens":10,"outputTokens":5,"sent":true}""");

        await Client(handler, reporter).CompleteAsync("prompt", "claude-sonnet-5");

        reporter.Last.Should().Be(new LlmUsage(10, 5, "claude-fable-5"));
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
            """{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","inputTokens":10,"outputTokens":5,"sent":true}""");

        var text = await Client(handler, new ThrowingReporter()).CompleteAsync("prompt");

        text.Should().Be("""{"action":"Buy"}""");
        text.Should().NotContain("Hold");
    }

    // 応答にトークンが無い（欠落）場合は 0 として扱う。
    // #303, IADR-0122: モデル名も欠落し得る（上流未更新・部分写像）。null のまま渡し、単価解決側で安全側へ倒す。
    [Fact]
    public async Task 応答にトークンが無い場合は_0_として計測する()
    {
        var reporter = new RecordingReporter();
        // モデル名も欠落させる（本試験の主題）。#335 の割当検証は計測より後段のため、計測の表明はそのまま成立する。
        await Client(new StubHandler(HttpStatusCode.OK, """{"text":"{}","sent":true}"""), reporter).CompleteAsync("p");

        reporter.Last.Should().Be(new LlmUsage(0, 0, null));
    }

    // ADR-0010（platform LLM ゲートウェイの越境ルーティング）: 要求に prompt/model/confidentiality/purpose を載せる。
    [Fact]
    public async Task 要求に_prompt_model_confidentiality_purpose_を載せる()
    {
        var handler = new CapturingHandler("""{"text":"{}","model":"claude-sonnet-5","inputTokens":1,"outputTokens":1,"sent":true}""");
        await Client(handler).CompleteAsync("私のプロンプト", "primary-model");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("prompt").GetString().Should().Be("私のプロンプト");
        doc.RootElement.GetProperty("model").GetString().Should().Be("primary-model");
        doc.RootElement.GetProperty("confidentiality").GetString().Should().Be("internal");
        doc.RootElement.GetProperty("purpose").GetString().Should().Be("trade-decision");
    }

    // #11, FR-11, IADR-0061 決定1: 受け入れ基準「プロンプト・入出力・根拠が全量ログに残る」。
    // 有効化時はプロンプト本文と LLM の生出力を記録する（事後に判断根拠を再構成できること）。
    [Fact]
    public async Task LogPrompts有効時_プロンプト本文と生出力を全量ログに残す()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\",\"rationale\":\"上昇基調\"}","model":"claude-sonnet-5","inputTokens":10,"outputTokens":5,"sent":true}""");

        await LoggingClient(handler, logger, logPrompts: true).CompleteAsync("私の長いプロンプト", "primary-model");

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("私の長いプロンプト");
        log.Should().Contain("上昇基調");
    }

    // 既定（オフ）ではプロンプト・生出力を残さない。プロンプトは保有ポジション・資金残枠等の機微を含むため、
    // 既定でログ基盤へ流さない（IADR-0061 決定1 の最小権限）。
    [Fact]
    public async Task LogPrompts既定は_プロンプトも生出力も残さない()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\",\"rationale\":\"上昇基調\"}","model":"claude-sonnet-5","inputTokens":10,"outputTokens":5,"sent":true}""");

        await LoggingClient(handler, logger, logPrompts: false).CompleteAsync("私の長いプロンプト", "primary-model");

        var log = string.Join("\n", logger.Messages);
        log.Should().NotContain("私の長いプロンプト");
        log.Should().NotContain("上昇基調");
    }

    // IADR-0061 決定1 の不変条件: 全量ログを有効にしても安全既定（IADR-0017）は変わらない。
    [Fact]
    public async Task LogPrompts有効でも_送信拒否や非2xxは_Hold_取引しない()
    {
        var logger = new RecordingLogger();
        (await LoggingClient(new StubHandler(HttpStatusCode.OK,
            """{"text":"拒否","sent":false}"""), logger, logPrompts: true).CompleteAsync("p"))
            .Should().Contain("Hold");
        (await LoggingClient(new StubHandler(HttpStatusCode.InternalServerError, ""), logger, logPrompts: true).CompleteAsync("p"))
            .Should().Contain("Hold");
        (await LoggingClient(new ThrowingHandler(), logger, logPrompts: true).CompleteAsync("p"))
            .Should().Contain("Hold");
    }

    // --- #247, FR-04, IADR-0104: 終了理由（stopReason）の評価 -------------------------------------

    // IADR-0104 決定2: 拒否は本文を読む前に評価し、本文が非空でも判断へ流さない（上流の破棄に依存しない多層防御）。
    [Fact]
    public async Task 拒否_refusal_は本文が非空でも判断へ流さず_Hold_取引しない()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Buy\",\"rationale\":\"拒否された断片\",\"referencePrice\":1000,\"stopLossDistancePerShare\":30}","model":"claude-sonnet-5","inputTokens":10,"outputTokens":5,"model":"claude-sonnet-5","sent":true,"stopReason":"refusal"}""");

        var text = await Client(handler).CompleteAsync("prompt");

        // 拒否された断片（Buy の判断材料）が一切返らないこと。
        text.Should().NotContain("Buy");
        text.Should().NotContain("拒否された断片");

        // FR-11: 判断へ倒す先は Hold であり、その理由として拒否が識別できること。
        var decision = TradeDecisionParser.Parse(text);
        decision.Action.Should().Be(TradeAction.Hold);
        decision.Rationale.Should().Contain("拒否");
    }

    [Theory]
    [InlineData("REFUSAL")]
    [InlineData("Refusal")]
    public async Task 拒否の判定は大小を無視する(string stopReason)
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            $$"""{"text":"{\"action\":\"Buy\",\"referencePrice\":1000,\"stopLossDistancePerShare\":30}","model":"claude-sonnet-5","sent":true,"stopReason":"{{stopReason}}"}""");

        TradeDecisionParser.Parse(await Client(handler).CompleteAsync("prompt")).Rationale.Should().Contain("拒否");
    }

    // IADR-0104 決定3: 送信拒否（Sent=false）／応答不正／空応答／上限到達／拒否が相互に区別できること。
    [Fact]
    public async Task 拒否_送信拒否_空応答_上限到達は_相互に区別できる理由になる()
    {
        var refused = await Client(new StubHandler(HttpStatusCode.OK,
            """{"text":"断片","model":"claude-sonnet-5","sent":true,"stopReason":"refusal"}""")).CompleteAsync("p");
        var notSent = await Client(new StubHandler(HttpStatusCode.OK,
            """{"text":"機密区分により送信できません","sent":false}""")).CompleteAsync("p");
        var empty = await Client(new StubHandler(HttpStatusCode.OK,
            """{"text":"","model":"claude-sonnet-5","sent":true,"stopReason":"end_turn"}""")).CompleteAsync("p");
        var maxTokens = await Client(new StubHandler(HttpStatusCode.OK,
            """{"text":"","model":"claude-sonnet-5","sent":true,"stopReason":"max_tokens"}""")).CompleteAsync("p");
        var malformed = await Client(new StubHandler(HttpStatusCode.OK, "not-json")).CompleteAsync("p");

        var rationales = new[] { refused, notSent, empty, maxTokens, malformed }
            .Select(t => TradeDecisionParser.Parse(t))
            .ToList();

        rationales.Should().AllSatisfy(d => d.Action.Should().Be(TradeAction.Hold));
        rationales.Select(d => d.Rationale).Should().OnlyHaveUniqueItems();
    }

    // IADR-0104 決定3: 全量ログが無効でも「上流が非空の断片を渡してきた」事実（本文の長さ）を残す。
    [Fact]
    public async Task 拒否は本文長つきで警告ログに残す_全量ログ無効でも本文自体は残さない()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"拒否された断片です","model":"claude-sonnet-5","sent":true,"stopReason":"refusal"}""");

        await LoggingClient(handler, logger, logPrompts: false).CompleteAsync("p");

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("refusal");
        log.Should().Contain("textLength=9"); // 本文の長さ（断片が届いた事実）は残す
        log.Should().NotContain("拒否された断片です");
    }

    // IADR-0104 決定4: 拒否は Sent=true かつ課金済み（実送信されている）。費用計測へ渡す（過少計上を避ける）。
    [Fact]
    public async Task 拒否でもトークンを費用計測へ渡す()
    {
        var reporter = new RecordingReporter();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"断片","model":"claude-sonnet-5","inputTokens":80,"outputTokens":12,"model":"claude-sonnet-5","sent":true,"stopReason":"refusal"}""");

        await Client(handler, reporter).CompleteAsync("p");

        reporter.Calls.Should().Be(1);
        reporter.Last.Should().Be(new LlmUsage(80, 12, "claude-sonnet-5"));
    }

    // IADR-0104 決定4: 上限到達で本文が空になる場合も思考トークンは課金済み（IADR-0101）。Sent=true なら計測する。
    [Fact]
    public async Task 空応答_上限到達でもトークンを費用計測へ渡す()
    {
        var reporter = new RecordingReporter();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"","model":"claude-sonnet-5","inputTokens":50,"outputTokens":4096,"model":"claude-sonnet-5","sent":true,"stopReason":"max_tokens"}""");

        await Client(handler, reporter).CompleteAsync("p");

        reporter.Calls.Should().Be(1);
        reporter.Last.Should().Be(new LlmUsage(50, 4096, "claude-sonnet-5"));
    }

    // IADR-0104 決定5: 上限到達は劣化であり拒否ではない。本文は破棄せず判断へ渡す（IADR-0101 の劣化観測を壊さない）。
    [Fact]
    public async Task 上限到達_max_tokens_は本文を破棄せず返し_劣化を警告ログに残す()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"{\"action\":\"Hold\",\"rationale\":\"様子見\"}","model":"claude-sonnet-5","model":"claude-sonnet-5","sent":true,"stopReason":"max_tokens"}""");

        var text = await LoggingClient(handler, logger, logPrompts: false).CompleteAsync("p");

        text.Should().Be("""{"action":"Hold","rationale":"様子見"}""");
        string.Join("\n", logger.Messages).Should().Contain("max_tokens");
    }

    // 非破壊: stopReason 未設定（上流未更新・未対応プロバイダ）・未知値・正常終了は現行挙動のまま本文を返す。
    [Theory]
    [InlineData("""{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","sent":true}""")]
    [InlineData("""{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","sent":true,"stopReason":null}""")]
    [InlineData("""{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","sent":true,"stopReason":"end_turn"}""")]
    [InlineData("""{"text":"{\"action\":\"Buy\"}","model":"claude-sonnet-5","sent":true,"stopReason":"future_reason"}""")]
    public async Task stopReason_欠落_未知値_正常終了は現行どおり本文を返す(string body)
    {
        (await Client(new StubHandler(HttpStatusCode.OK, body)).CompleteAsync("p"))
            .Should().Be("""{"action":"Buy"}""");
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
