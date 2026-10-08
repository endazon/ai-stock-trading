using System.Net;
using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Domain;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TradeDecisionService.Tests;

// FR-04, FR-09, FR-11, #1267, IADR-0517: ゲートウェイの Sent=false を「機密区分による縮退」と断定せず、
// 申告（RoutingReason・Text の要約・原因の種類・上流の状態コード）のまま ログ・Hold の判断理由・通知へ運ぶ。
//
// 2026-10-07 の PoC: 判断 132 件が `Sent=false・機密区分による縮退` のログだけを残して Hold に固定され、
// 同じ purpose・機密区分で直前まで成功していたため誤帰属だった。原因を追う材料（Text / RoutingReason）は捨てられていた。
public class LlmGatewayUnsentReasonTests
{
    // 現行の基盤（MSP CompletionUseCase）が返す 3 形。FailureKind は MSP#1819 の予定のフィールド（名前は未確定）。
    private const string UpstreamText = "呼び出し先 anthropic-managed が現在利用できません。";
    private const string RoutingAllowed = "internal は anthropic-managed へ送信可";
    private const string EgressReason = "restricted は外部 LLM へ送信不可";

    private sealed class FakeTransport(LlmCompletionExchange exchange) : ILlmCompletionTransport
    {
        public Task<LlmCompletionExchange> CompleteAsync(
            LlmCompletionCall call, TimeSpan? deadline = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(exchange);
    }

    private sealed class ThrowingTransport : ILlmCompletionTransport
    {
        public Task<LlmCompletionExchange> CompleteAsync(
            LlmCompletionCall call, TimeSpan? deadline = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("接続できません");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class RecordingLogger : ILogger<HttpLlmCompletionClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class RecordingNotifier : ILlmGatewayUnsentNotifier
    {
        public List<(string Purpose, LlmGatewayUnsentCause Cause)> Unsent { get; } = [];
        public int Sent { get; private set; }

        public Task ReportUnsentAsync(string purpose, LlmGatewayUnsentCause cause, CancellationToken cancellationToken = default)
        {
            Unsent.Add((purpose, cause));
            return Task.CompletedTask;
        }

        public Task ReportSentAsync(CancellationToken cancellationToken = default)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : ILlmGatewayUnsentNotifier
    {
        public Task ReportUnsentAsync(string purpose, LlmGatewayUnsentCause cause, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("通知の発行に失敗");

        public Task ReportSentAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("通知の発行に失敗");
    }

    private static HttpLlmCompletionClient Client(
        ILlmCompletionTransport transport, ILogger<HttpLlmCompletionClient>? logger = null,
        ILlmGatewayUnsentNotifier? notifier = null) =>
        new(transport, logger ?? NullLogger<HttpLlmCompletionClient>.Instance, "internal", purposeOverride: null,
            new NoOpLlmUsageReporter(), unsentNotifier: notifier);

    private static HttpLlmCompletionClient RestClient(string body, ILogger<HttpLlmCompletionClient>? logger = null) =>
        Client(new RestLlmCompletionTransport(new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://llm-gateway") }),
            logger);

    private static LlmCompletionExchange Unsent(
        string? text, string? routingReason, LlmGatewayUnsentKind? kind = null, int? status = null) =>
        LlmCompletionExchange.Completed(new LlmCompletionPayload(
            text, Sent: false, Model: "", StopReason: null, InputTokens: 0, OutputTokens: 0, routingReason, kind, status));

    private static LlmCompletionExchange SentOk() =>
        LlmCompletionExchange.Completed(new LlmCompletionPayload(
            """{"action":"Hold","rationale":"様子見"}""", Sent: true, "claude-haiku-4-5", "end_turn", 10, 5));

    private static string RationaleOf(string output) => TradeDecisionParser.Parse(output).Rationale;

    // ---- T-04-001〜004: Sent=false の各変種 -------------------------------------------------------------

    // T-04-001: 越境の拒否（基盤は Text と RoutingReason に同じ理由を載せる）。同じ文を 2 度書かない。
    [Fact]
    public async Task T_04_001_越境の拒否は種別と理由を判断理由へ載せる()
    {
        var output = await Client(new FakeTransport(Unsent(EgressReason, EgressReason, LlmGatewayUnsentKind.EgressDenied)))
            .CompleteAsync("p", purpose: LlmPurposes.TradeDecisionScreening);

        var decision = TradeDecisionParser.Parse(output);
        decision.Action.Should().Be(TradeAction.Hold);
        decision.Rationale.Should().StartWith(HttpLlmCompletionClient.UnsentRationalePrefix)
            .And.Contain("種別: 越境の拒否").And.Contain(EgressReason);
        decision.Rationale.Should().NotContain("ゲートウェイ:", "理由と本文が同じなら重ねない");
    }

    // T-04-002: 上流の不調（2026-10-07 の形に最も近い）。🔴 「機密区分」と書かない。
    [Fact]
    public async Task T_04_002_上流の不調は状態コードと本文の説明を載せ_機密区分と書かない()
    {
        var output = await Client(new FakeTransport(Unsent(UpstreamText, RoutingAllowed, LlmGatewayUnsentKind.UpstreamError, 429)))
            .CompleteAsync("p");

        var rationale = RationaleOf(output);
        rationale.Should().Contain("種別: 上流の不調").And.Contain("上流 429")
            .And.Contain(RoutingAllowed).And.Contain("呼び出し先 anthropic-managed が現在利用できません");
        rationale.Should().NotContain("機密区分");
    }

    // T-04-003: プロバイダ未登録。
    [Fact]
    public async Task T_04_003_プロバイダ未登録は種別を載せる()
    {
        var output = await Client(new FakeTransport(Unsent(
            "呼び出し先プロバイダ anthropic が未登録のため送信できません。", RoutingAllowed, LlmGatewayUnsentKind.ProviderMissing)))
            .CompleteAsync("p");

        RationaleOf(output).Should().Contain("種別: プロバイダ未登録").And.Contain("未登録のため送信できません");
    }

    // T-04-004: 原因の種類が無い（現行の基盤。MSP#1819 の前）。🔴 推測で埋めず「種別不明」と書き、申告は運ぶ。
    [Fact]
    public async Task T_04_004_原因の種類が無ければ種別不明と書き_理由と本文は運ぶ()
    {
        var output = await Client(new FakeTransport(Unsent(UpstreamText, RoutingAllowed))).CompleteAsync("p");

        var rationale = RationaleOf(output);
        rationale.Should().Contain("種別: 種別不明").And.Contain(RoutingAllowed).And.Contain("現在利用できません");
        rationale.Should().NotContain("機密区分").And.NotContain("上流 ");
    }

    // 否定形: 申告が何も無い（Text も RoutingReason も空）でも落ちず、「申告なし」と書く（空の理由にしない）。
    [Fact]
    public async Task T_04_004_申告が何も無くても申告なしと書いて_Hold()
    {
        var output = await Client(new FakeTransport(Unsent(null, "  "))).CompleteAsync("p");

        var decision = TradeDecisionParser.Parse(output);
        decision.Action.Should().Be(TradeAction.Hold);
        decision.Rationale.Should().Contain("（ゲートウェイの申告なし）").And.NotContain("機密区分");
    }

    // ---- T-04-005〜006: 予定のフィールド（MSP#1819）の寛容な読み取り（REST の実 JSON を通す） ---------------

    // T-04-005: 既知の値（大小・区切りの差を許す）は読む。
    [Theory]
    [InlineData("\"EgressDenied\"", "越境の拒否")]
    [InlineData("\"upstream_error\"", "上流の不調")]
    [InlineData("\"provider-missing\"", "プロバイダ未登録")]
    public async Task T_04_005_原因の種類の既知の値は読む(string json, string label)
    {
        var output = await RestClient($$"""{"text":"x","sent":false,"routingReason":"r","failureKind":{{json}}}""").CompleteAsync("p");

        RationaleOf(output).Should().Contain($"種別: {label}");
    }

    // 🔴 T-04-005（否定形）: 未知の値・序数・想定外の型は例外にせず種別不明へ倒し、Sent=false の Hold のまま
    // （「応答不正」へ化けない）。型を enum にすると JsonException になり、送信不可の 1 件が別の系統へ化ける。
    [Theory]
    [InlineData("\"QuotaExceeded\"")]
    [InlineData("2")]
    [InlineData("\"2\"")]
    [InlineData("{\"code\":1}")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task T_04_005_原因の種類の未知の値は例外にせず種別不明(string json)
    {
        var output = await RestClient($$"""{"text":"x","sent":false,"routingReason":"r","failureKind":{{json}}}""").CompleteAsync("p");

        var rationale = RationaleOf(output);
        rationale.Should().StartWith(HttpLlmCompletionClient.UnsentRationalePrefix).And.Contain("種別: 種別不明");
        rationale.Should().NotContain("応答不正");
    }

    // T-04-006: 上流の状態コードは数値・数字の文字列・別名（upstreamStatus）を読み、範囲外・非数は捨てる。
    [Theory]
    [InlineData("\"upstreamStatusCode\":503", "上流 503")]
    [InlineData("\"upstreamStatusCode\":\"401\"", "上流 401")]
    [InlineData("\"upstreamStatus\":429", "上流 429")]
    public async Task T_04_006_上流の状態コードを読む(string field, string expected)
    {
        var output = await RestClient($$"""{"text":"x","sent":false,"routingReason":"r",{{field}}}""").CompleteAsync("p");

        RationaleOf(output).Should().Contain(expected);
    }

    [Theory]
    [InlineData("\"upstreamStatusCode\":42")]
    [InlineData("\"upstreamStatusCode\":600")]
    [InlineData("\"upstreamStatusCode\":\"abc\"")]
    [InlineData("\"upstreamStatusCode\":5.5")]
    [InlineData("\"upstreamStatusCode\":[503]")]
    public async Task T_04_006_上流の状態コードの不正値は捨てる(string field)
    {
        var output = await RestClient($$"""{"text":"x","sent":false,"routingReason":"r",{{field}}}""").CompleteAsync("p");

        var rationale = RationaleOf(output);
        rationale.Should().StartWith(HttpLlmCompletionClient.UnsentRationalePrefix).And.NotContain("上流 ");
    }

    // ---- T-04-007: 要約（秘密・行区切り・長さ） --------------------------------------------------------

    [Fact]
    public async Task T_04_007_要約は秘密を伏せ_1行に収め_切り詰める()
    {
        var text = "upstream failed: Authorization: Bearer abc.def.ghi api_key=zzz-secret-123 sk-ant-api03-ABCDEFGHIJ\n"
            + "2026-10-07 [INF] 偽の行 " + new string('x', 40) + " " + new string('長', 400);
        var logger = new RecordingLogger();

        var output = await Client(new FakeTransport(Unsent(text, "r")), logger).CompleteAsync("p");

        var rationale = RationaleOf(output);
        var log = logger.Entries.Single(e => e.Level == LogLevel.Warning).Message;
        foreach (var leaked in new[] { "abc.def.ghi", "zzz-secret-123", "sk-ant-api03-ABCDEFGHIJ", new string('x', 40) })
        {
            rationale.Should().NotContain(leaked);
            log.Should().NotContain(leaked);
        }

        rationale.Should().Contain("***").And.Contain("upstream failed");
        rationale.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029').Should().BeFalse();
        log.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029').Should().BeFalse();
        // 要約は SummaryMaxLength 文字＋切り詰めの注記まで。判断理由の全体も上限で収まる。
        rationale.Length.Should().BeLessThan(LlmGatewayUnsent.SummaryMaxLength * 2 + 120);
        rationale.Should().Contain("truncated");
    }

    // ---- T-04-008〜009: ログ・判断理由への伝播 ------------------------------------------------------

    // T-04-008: Warning ログに purpose・種別・状態・理由・本文の要約が構造化して載り、「機密区分」と断定しない。
    [Fact]
    public async Task T_04_008_警告ログに申告が載り_機密区分と断定しない()
    {
        var logger = new RecordingLogger();

        await Client(new FakeTransport(Unsent(UpstreamText, RoutingAllowed, LlmGatewayUnsentKind.UpstreamError, 502)), logger)
            .CompleteAsync("p", purpose: LlmPurposes.TradeDecisionScreening);

        var log = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject.Message;
        log.Should().Contain("Sent=false").And.Contain(LlmPurposes.TradeDecisionScreening)
            .And.Contain("UpstreamError").And.Contain("502").And.Contain(RoutingAllowed).And.Contain("現在利用できません");
        log.Should().NotContain("機密区分");
    }

    // T-04-009: 利用者が見る判断の理由（一次の打ち切り・二次の Hold。FR-11 ログの rationale）へ届く。
    [Fact]
    public async Task T_04_009_一次の打ち切りと二次の判断の理由へ申告が届く()
    {
        var output = await Client(new FakeTransport(Unsent(UpstreamText, RoutingAllowed, LlmGatewayUnsentKind.UpstreamError)))
            .CompleteAsync("p");

        var screening = TradeDecisionParser.ParseScreening(output);
        screening.IsInterested.Should().BeFalse();
        screening.IsUnparseable.Should().BeFalse("送信不可は解析不能と区別する（IADR-0248）");
        screening.AsHold.Rationale.Should().Contain("上流の不調").And.Contain(RoutingAllowed);

        var detailed = TradeDecisionParser.ParseDetailed(output, signedHeldQuantity: 10);
        detailed.IsUnparseable.Should().BeFalse();
        detailed.Decision.Rationale.Should().Contain("上流の不調");
    }

    // 否定形: 引用符・バックスラッシュを含む申告でも JSON が壊れない（手で連結しない）。
    [Fact]
    public async Task T_04_009_引用符を含む申告でも判断の_JSON_は壊れない()
    {
        var output = await Client(new FakeTransport(Unsent("bad \"quote\" \\ end", "r\"}"))).CompleteAsync("p");

        TradeDecisionParser.ParseDetailed(output, signedHeldQuantity: null).IsUnparseable.Should().BeFalse();
        RationaleOf(output).Should().Contain("bad \"quote\"");
    }

    // ---- T-04-010〜011: 通知ポートへの受け渡し ----------------------------------------------------------

    [Fact]
    public async Task T_04_010_Sent_false_は通知ポートへ原因つきで_Sent_true_は回復の契機として渡す()
    {
        var notifier = new RecordingNotifier();

        await Client(new FakeTransport(Unsent(UpstreamText, RoutingAllowed, LlmGatewayUnsentKind.UpstreamError, 429)), notifier: notifier)
            .CompleteAsync("p", purpose: LlmPurposes.TradeDecisionScreening);
        await Client(new FakeTransport(SentOk()), notifier: notifier)
            .CompleteAsync("p", purpose: LlmPurposes.TradeDecisionScreening);

        var (purpose, cause) = notifier.Unsent.Should().ContainSingle().Subject;
        purpose.Should().Be(LlmPurposes.TradeDecisionScreening);
        cause.Kind.Should().Be(LlmGatewayUnsentKind.UpstreamError);
        cause.UpstreamStatusCode.Should().Be(429);
        cause.RoutingReason.Should().Be(RoutingAllowed);
        notifier.Sent.Should().Be(1);
    }

    // 🔴 否定形: 非 2xx・応答不正・例外は Sent=false の連続を進めも戻しもしない（別の Hold の系統）。
    [Fact]
    public async Task T_04_010_非2xx_応答不正_例外は通知ポートへ渡さない()
    {
        var notifier = new RecordingNotifier();

        await Client(new FakeTransport(LlmCompletionExchange.Failed(LlmFailureKind.Other, "503")), notifier: notifier).CompleteAsync("p");
        await Client(new FakeTransport(LlmCompletionExchange.Malformed()), notifier: notifier).CompleteAsync("p");
        await Client(new ThrowingTransport(), notifier: notifier).CompleteAsync("p");

        notifier.Unsent.Should().BeEmpty();
        notifier.Sent.Should().Be(0);
    }

    // T-04-011: 通知の失敗で Hold も成功応答も壊さない（best-effort）。
    [Fact]
    public async Task T_04_011_通知の失敗で判断を壊さない()
    {
        var unsent = await Client(new FakeTransport(Unsent(UpstreamText, RoutingAllowed)), notifier: new ThrowingNotifier())
            .CompleteAsync("p");
        var sent = await Client(new FakeTransport(SentOk()), notifier: new ThrowingNotifier())
            .CompleteAsync("p", purpose: LlmPurposes.TradeDecisionScreening);

        RationaleOf(unsent).Should().StartWith(HttpLlmCompletionClient.UnsentRationalePrefix);
        sent.Should().Contain("様子見");
    }
}
