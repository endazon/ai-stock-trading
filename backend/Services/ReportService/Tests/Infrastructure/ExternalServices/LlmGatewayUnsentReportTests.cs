using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Infrastructure.ExternalServices;
using Xunit;
using static ReportService.Tests.LlmReportPolicyReviserTests;

namespace ReportService.Tests;

// FR-06, FR-14, FR-11, #1267, IADR-0517: 報告書の散文・方針の改訂も、ゲートウェイの Sent=false を
// 「機密区分による縮退」と断定せず、申告（理由・本文の説明の要約・原因の種類）のまま残す（取引判断と同じ要約）。
public class LlmGatewayUnsentReportTests
{
    private const string UpstreamText = "呼び出し先 anthropic-managed が現在利用できません。";
    private const string RoutingAllowed = "internal は anthropic-managed へ送信可";

    private static readonly ReportNarrativeContext Ctx = new(
        ReportKind.Daily, "daily-2026-10-07", "2026-10-07", ["US"],
        new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌日は継続");

    private static LlmCompletionExchange Unsent(LlmGatewayUnsentKind? kind = null, int? status = null) =>
        LlmCompletionExchange.Completed(new LlmCompletionPayload(
            UpstreamText, Sent: false, Model: "", StopReason: null, InputTokens: 0, OutputTokens: 0,
            RoutingAllowed, kind, status));

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    // T-04-020: 報告書の散文。プレースホルダへ倒すのは従来どおり、ログは申告を運び「機密区分」と書かない。
    [Fact]
    public async Task T_04_020_報告書の散文は申告をログへ残しプレースホルダへ倒す()
    {
        var logger = new RecordingLogger<HttpReportNarrativeDrafter>();
        var drafter = new HttpReportNarrativeDrafter(
            new FakeTransport(Unsent(LlmGatewayUnsentKind.UpstreamError, 503)), logger, "internal", null);

        (await drafter.DraftNarrativeAsync(Ctx)).Should().Be(ReportNarrativeDefaults.PlaceholderText);

        var log = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject.Message;
        log.Should().Contain("Sent=false").And.Contain("UpstreamError").And.Contain("503")
            .And.Contain(RoutingAllowed).And.Contain("現在利用できません");
        log.Should().NotContain("機密区分");
    }

    // T-04-021: 方針の改訂。利用者へ返す理由に申告の要約が載り、「縮退中」と一括りにしない。案なし（Refused）は不変。
    [Fact]
    public async Task T_04_021_方針の改訂は利用者への理由へ申告を載せる()
    {
        var reviser = new LlmReportPolicyReviser(
            new FakeTransport(Unsent()), NullLogger<LlmReportPolicyReviser>.Instance, "internal", purposeOverride: null,
            TimeSpan.FromSeconds(5), new RecordingUsage(), new NoOpGovernance());

        var outcome = await reviser.ReviseAsync(new PolicyRevisionContext(
            ReportKind.Daily, "daily-2026-10-07", "現状維持", new ParentPolicyReference("weekly-2026-W41", "週の方針"), "積極的に"));

        outcome.Succeeded.Should().BeFalse();
        outcome.Failure.Should().Be(PolicyRevisionFailure.Refused);
        outcome.Message.Should().StartWith("AI へ送信できませんでした").And.Contain("種別: 種別不明")
            .And.Contain(RoutingAllowed).And.Contain("現在利用できません");
        outcome.Message.Should().NotContain("縮退中").And.NotContain("機密区分");
    }
}
