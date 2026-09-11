using AiStockTrading.Shared.Infrastructure.Grpc.LlmGateway.V1;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.Shared.Contracts.Llm;
using ReportService.Features.Reports;
using ReportService.Domain;
using ReportService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GrpcStatusCode = Grpc.Core.StatusCode;

namespace ReportService.Tests;

// FR-06/16, NFR-05, MSP:ADR-0029, IADR-0071, IADR-0123, IADR-0284, IADR-0323 決定 3, IADR-0328, IADR-0332, #746:
// 報告書散文の輸送を east-west gRPC に替えても、**倒れ先と記録の原因は変わらない**。
// あわせて、**種別別タイムアウト（IADR-0123）が gRPC の deadline に載る**ことを固定する
// —— これが落ちると週報・月報が 30 秒（あるいは無制限）で切られ、IADR-0123 が是正した
// 「上位方針の空洞化」が別の輸送で再発する。
public class GrpcReportNarrativeDrafterTests
{
    private static readonly ReportNarrativeContext Daily = new(
        ReportKind.Daily, "daily-2026-09-11", "2026-09-11", ["JP"],
        new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌日は継続");

    private static readonly ReportNarrativeContext Weekly = new(
        ReportKind.Weekly, "weekly-2026-W37", "2026-09-07〜2026-09-11", ["JP"],
        new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌週は継続");

    // IADR-0123 決定 2 の組込既定（日報 30 秒 / 週報・月報 120 秒）と同じ解決を与える。
    private static TimeSpan TimeoutFor(ReportKind kind) =>
        kind == ReportKind.Daily ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(120);

    private static HttpReportNarrativeDrafter Drafter(
        LlmCompletion.LlmCompletionClient grpc,
        ILogger<HttpReportNarrativeDrafter>? logger = null,
        TimeProvider? time = null) =>
        new(new GrpcLlmCompletionTransport(grpc, TimeSpan.FromSeconds(120), time),
            logger ?? NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", "report-narrative",
            logPrompts: false, timeoutFor: TimeoutFor);

    // ---- 認可の失敗（陽性） --------------------------------------------------------------------

    [Theory]
    [InlineData(GrpcStatusCode.Unauthenticated)]
    [InlineData(GrpcStatusCode.PermissionDenied)]
    public async Task 認可の失敗は資格情報を名指しし_モデルの可否として記録しない(GrpcStatusCode status)
    {
        var logger = new RecordingLogger();

        var text = await Drafter(Failing(status), logger).DraftNarrativeAsync(Daily);

        // ① 倒れ先は不変（安全側＝捏造しない定型散文。報告書は発注を伴わない）。
        text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        // ② 原因を名指しする。
        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("認可");
        log.Should().Contain("LlmGateway:Auth");
        // ③ 🔴 モデルの可否として記録しない。
        log.Should().NotContain("モデル不可");
    }

    // 陰性対照: 認可以外は従来どおりの汎用メッセージ（「常に認可と書く」実装で上が緑にならない）。
    [Theory]
    [InlineData(GrpcStatusCode.Internal)]
    [InlineData(GrpcStatusCode.Unavailable)]
    [InlineData(GrpcStatusCode.Unimplemented)]
    public async Task 認可以外の失敗は従来どおりの記録のまま(GrpcStatusCode status)
    {
        var logger = new RecordingLogger();

        var text = await Drafter(Failing(status), logger).DraftNarrativeAsync(Daily);

        text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("非 2xx");
        log.Should().NotContain("認可");
    }

    // ---- 種別別タイムアウト → deadline（陽性・陰性の対照） --------------------------------------

    [Fact]
    public async Task 週報の上限が_deadline_として載る()
    {
        var now = new DateTimeOffset(2026, 9, 11, 6, 0, 0, TimeSpan.Zero);
        var grpc = Responding(new CompleteResponse { Text = "週次の所感。", Sent = true });

        await Drafter(grpc, time: new FixedTime(now)).DraftNarrativeAsync(Weekly);

        grpc.LastOptions.Deadline.Should().Be(now.UtcDateTime.AddSeconds(120));
    }

    // 陰性対照: 日報は 30 秒。**種別で値が変わること**を示さないと「常に 120 秒」でも上が緑になる。
    [Fact]
    public async Task 日報の上限は週報と別の値で_deadline_に載る()
    {
        var now = new DateTimeOffset(2026, 9, 11, 6, 0, 0, TimeSpan.Zero);
        var grpc = Responding(new CompleteResponse { Text = "日次の所感。", Sent = true });

        await Drafter(grpc, time: new FixedTime(now)).DraftNarrativeAsync(Daily);

        grpc.LastOptions.Deadline.Should().Be(now.UtcDateTime.AddSeconds(30));
        grpc.LastOptions.Deadline.Should().NotBe(now.UtcDateTime.AddSeconds(120));
    }

    // 上限に当たったら（deadline 超過）プレースホルダへ倒し、**秒数と種別を残す**（IADR-0123 決定 5）。
    [Fact]
    public async Task deadline_超過はプレースホルダへ倒し_種別と秒数を残す()
    {
        var logger = new RecordingLogger();

        var text = await Drafter(Failing(GrpcStatusCode.DeadlineExceeded), logger).DraftNarrativeAsync(Weekly);

        text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("タイムアウト");
        log.Should().Contain("Weekly");
        log.Should().Contain("120");
    }

    // ---- 応答の写し ------------------------------------------------------------------------------

    [Fact]
    public async Task 送信成功は散文本文を返す()
    {
        var grpc = Responding(new CompleteResponse
        {
            Text = "本日は堅調な地合いでした。",
            Sent = true,
            Model = "claude-sonnet-5",
            StopReason = "end_turn",
        });

        (await Drafter(grpc).DraftNarrativeAsync(Daily)).Should().Be("本日は堅調な地合いでした。");
    }

    // 縮退（sent=false）は例外ではなく応答で来る。倒れ先は REST と同じプレースホルダ。
    [Fact]
    public async Task 縮退はプレースホルダへ倒す()
    {
        var grpc = Responding(new CompleteResponse { Text = "拒否", Sent = false });

        (await Drafter(grpc).DraftNarrativeAsync(Daily)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
    }

    // ---- test double -----------------------------------------------------------------------------

    private static FakeCompletionClient Responding(CompleteResponse response) => new(_ => response);

    private static FakeCompletionClient Failing(GrpcStatusCode status) =>
        new(_ => throw new RpcException(new Status(status, "test")));

    private sealed class FakeCompletionClient(Func<CompleteRequest, CompleteResponse> respond)
        : LlmCompletion.LlmCompletionClient
    {
        public CallOptions LastOptions { get; private set; }

        public override AsyncUnaryCall<CompleteResponse> CompleteAsync(
            CompleteRequest request, CallOptions options)
        {
            LastOptions = options;

            Task<CompleteResponse> response;
            try
            {
                response = Task.FromResult(respond(request));
            }
            catch (Exception ex)
            {
                response = Task.FromException<CompleteResponse>(ex);
            }

            return new AsyncUnaryCall<CompleteResponse>(
                response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLogger : ILogger<HttpReportNarrativeDrafter>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
