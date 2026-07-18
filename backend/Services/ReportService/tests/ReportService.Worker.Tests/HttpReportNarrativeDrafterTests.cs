using System.Net;
using System.Text.Json;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Report.Worker.Foundation.Adapters;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Report.Worker.Tests;

// FR-06/16, IADR-0071 決定1: platform LLM ゲートウェイ POST /complete を呼ぶ実 LLM 散文ドラフトの写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。送信拒否/失敗/タイムアウト/空応答はプレースホルダ散文へ倒す。
// 数値には一切関与しない（数値はコード集計が権威・FR-16）。
public class HttpReportNarrativeDrafterTests
{
    private static readonly ReportNarrativeContext Ctx = new(
        ReportKind.Daily, "daily-2026-07-18", "2026-07-18", ["JP"],
        new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌日は継続");

    private static HttpReportNarrativeDrafter Drafter(HttpMessageHandler handler, ILogger<HttpReportNarrativeDrafter>? logger = null, bool logPrompts = false) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            logger ?? NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", "report-narrative", logPrompts);

    [Fact]
    public async Task 送信成功_Sent_true_は_散文本文を返す()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"本日は堅調な地合いでした。","model":"claude","sent":true}""");

        var text = await Drafter(handler).DraftNarrativeAsync(Ctx);

        text.Should().Be("本日は堅調な地合いでした。");
        handler.LastPath.Should().Be("/complete");
    }

    [Fact]
    public async Task 送信拒否_Sent_false_は_プレースホルダ散文へ倒す()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"text":"拒否","sent":false}""");
        (await Drafter(handler).DraftNarrativeAsync(Ctx)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
    }

    [Fact]
    public async Task 非_2xx_は_プレースホルダ散文へ倒す()
    {
        (await Drafter(new StubHandler(HttpStatusCode.InternalServerError, "")).DraftNarrativeAsync(Ctx))
            .Should().Be(ReportNarrativeDefaults.PlaceholderText);
    }

    [Fact]
    public async Task 例外_不達_は_プレースホルダ散文へ倒す()
    {
        (await Drafter(new ThrowingHandler()).DraftNarrativeAsync(Ctx)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
    }

    [Fact]
    public async Task 空_不正ボディの_200_は_プレースホルダ散文へ倒す()
    {
        (await Drafter(new StubHandler(HttpStatusCode.OK, "")).DraftNarrativeAsync(Ctx)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
        (await Drafter(new StubHandler(HttpStatusCode.OK, "not-json")).DraftNarrativeAsync(Ctx)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
    }

    [Fact]
    public async Task タイムアウト_応答遅延_は_プレースホルダ散文へ倒す()
    {
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)))
        {
            BaseAddress = new Uri("http://llm-gateway"),
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        var drafter = new HttpReportNarrativeDrafter(http, NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", "report-narrative");
        (await drafter.DraftNarrativeAsync(Ctx)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
    }

    [Fact]
    public async Task 要求に_prompt_confidentiality_purpose_を載せる()
    {
        var handler = new CapturingHandler("""{"text":"散文","sent":true}""");
        await Drafter(handler).DraftNarrativeAsync(Ctx);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("prompt").GetString().Should().Contain("日報");
        doc.RootElement.GetProperty("confidentiality").GetString().Should().Be("internal");
        doc.RootElement.GetProperty("purpose").GetString().Should().Be("report-narrative");
    }

    [Fact]
    public async Task LogPrompts有効時_プロンプトと生出力を全量ログに残す()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK, """{"text":"堅調でした。","model":"claude","sent":true}""");

        await Drafter(handler, logger, logPrompts: true).DraftNarrativeAsync(Ctx);

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("日報");
        log.Should().Contain("堅調でした。");
    }

    [Fact]
    public async Task LogPrompts既定は_プロンプトも生出力も残さない()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK, """{"text":"堅調でした。","model":"claude","sent":true}""");

        await Drafter(handler, logger, logPrompts: false).DraftNarrativeAsync(Ctx);

        var log = string.Join("\n", logger.Messages);
        log.Should().NotContain("堅調でした。");
    }

    private sealed class RecordingLogger : ILogger<HttpReportNarrativeDrafter>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
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
