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

    // 既存テストは purpose 上書きあり（従来挙動）を既定とする。種別ごとの purpose は purposeOverride: null で検証する。
    private static HttpReportNarrativeDrafter Drafter(
        HttpMessageHandler handler,
        ILogger<HttpReportNarrativeDrafter>? logger = null,
        bool logPrompts = false,
        string? purposeOverride = "report-narrative") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            logger ?? NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", purposeOverride, logPrompts);

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

    // T-5, FR-06/16, IADR-0123 決定1, #308: タイムアウトは**種別ごとに**効く。従来はサービス共通の 1 本だったため、
    // 重いモデルが割り当たる週報・月報（IADR-0120 / MSP#422）が日報と同じ 30 秒で打ち切られ、所感が恒常的に
    // プレースホルダへ縮退していた。同一インスタンス・同一の応答遅延で、日報は打ち切られ週報は通ることを固定する。
    [Fact]
    public async Task タイムアウトは報告書種別ごとに効く_日報は打ち切られ週報は通る()
    {
        var handler = new DelayingRespondingHandler(
            TimeSpan.FromMilliseconds(600), """{"text":"週次の所感です。","sent":true}""");
        // HttpClient 自体の上限は十分長くし、種別ごとの打ち切りだけを観測する。
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway"), Timeout = TimeSpan.FromSeconds(30) };
        var drafter = new HttpReportNarrativeDrafter(
            http, NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", null, logPrompts: false,
            timeoutFor: kind => kind == ReportKind.Daily ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(20));

        (await drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Daily }))
            .Should().Be(ReportNarrativeDefaults.PlaceholderText);
        (await drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Weekly, PeriodKey = "weekly-2026-W31" }))
            .Should().Be("週次の所感です。");
    }

    // T-6, IADR-0123 決定5, #308: 縮退の WRN は「タイムアウトした」しか言わず、どの上限で切られたのかが
    // 運用中に分からなかった。種別ごとに上限が変わる以上、種別と発火した秒数をログに残す。
    [Fact]
    public async Task タイムアウト縮退のログに種別と発火した秒数を残す()
    {
        var logger = new RecordingLogger();
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(30)))
        {
            BaseAddress = new Uri("http://llm-gateway"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        var drafter = new HttpReportNarrativeDrafter(
            http, logger, "internal", null, logPrompts: false,
            timeoutFor: _ => TimeSpan.FromMilliseconds(500));

        await drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Monthly, PeriodKey = "monthly-2026-07" });

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("タイムアウト");
        log.Should().Contain("Monthly");
        log.Should().Contain("0.5");
    }

    // 呼び出し側のキャンセル（停止要求）はタイムアウトと取り違えず、そのまま伝播する（縮退で握り潰さない）。
    [Fact]
    public async Task 呼び出し側のキャンセルは縮退せず伝播する()
    {
        using var cts = new CancellationTokenSource();
        var http = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(30)))
        {
            BaseAddress = new Uri("http://llm-gateway"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        var drafter = new HttpReportNarrativeDrafter(
            http, NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", null, logPrompts: false,
            timeoutFor: _ => TimeSpan.FromSeconds(20));

        var draft = drafter.DraftNarrativeAsync(Ctx, cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => draft).Should().ThrowAsync<OperationCanceledException>();
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

    // T-2, FR-06/11, IADR-0120 決定1, #291: 構成の purpose 上書きが無いとき、送出する purpose は
    // **要求ごとに種別から決まる**。従来は単一の固定値を送っており、基盤の PurposeModels に該当
    // エントリが無いため 3 種別すべてが DefaultModel へ着地していた（種別がルーティングに届かない）。
    [Theory]
    [InlineData(ReportKind.Daily, "report-daily")]
    [InlineData(ReportKind.Weekly, "report-weekly")]
    [InlineData(ReportKind.Monthly, "report-monthly")]
    public async Task 上書き未設定なら種別ごとのpurposeを送る(ReportKind kind, string expected)
    {
        var handler = new CapturingHandler("""{"text":"散文","sent":true}""");
        await Drafter(handler, purposeOverride: null).DraftNarrativeAsync(Ctx with { Kind = kind });

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("purpose").GetString().Should().Be(expected);
    }

    // T-3, IADR-0120 決定2: LlmGateway:Purpose を明示設定したデプロイでは**全種別へ上書き適用**する。
    // 構成値を単に削ると設定済みのデプロイで挙動が変わるため、既定値だけを外し上書きの意味を残す。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Monthly)]
    public async Task 上書きが設定されていれば全種別へ適用する(ReportKind kind)
    {
        var handler = new CapturingHandler("""{"text":"散文","sent":true}""");
        await Drafter(handler, purposeOverride: "report-narrative").DraftNarrativeAsync(Ctx with { Kind = kind });

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("purpose").GetString().Should().Be("report-narrative");
    }

    // IADR-0071 / IADR-0120 決定1: モデルの決定権は基盤の LlmRouter に残す。AST はモデル ID を持たない
    // （持つと NonZdrModels による除外や版数改定へ追随できず、Models 許可一覧との整合も崩れる）。
    [Fact]
    public async Task モデルは明示せず基盤のルーティングに委ねる()
    {
        var handler = new CapturingHandler("""{"text":"散文","sent":true}""");
        await Drafter(handler, purposeOverride: null).DraftNarrativeAsync(Ctx with { Kind = ReportKind.Monthly });

        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetProperty("model").ValueKind.Should().Be(JsonValueKind.Null);
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

    // --- #247, FR-06, IADR-0104: 終了理由（stopReason）の評価 -------------------------------------

    // IADR-0104 決定2: 拒否は本文を読む前に評価し、本文が非空でも成果物にしない（上流の破棄に依存しない多層防御）。
    [Fact]
    public async Task 拒否_refusal_は本文が非空でも成果物にせずプレースホルダ散文へ倒す()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"拒否された散文の断片","model":"claude","sent":true,"stopReason":"refusal"}""");

        var text = await Drafter(handler).DraftNarrativeAsync(Ctx);

        text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        text.Should().NotContain("拒否された散文の断片");
    }

    // IADR-0104 決定3: 拒否は送信拒否・空応答と区別できるログに残す（本文長で断片到達の事実も残す）。
    [Fact]
    public async Task 拒否は本文長つきで警告ログに残す_全量ログ無効でも本文自体は残さない()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"拒否された散文の断片","sent":true,"stopReason":"refusal"}""");

        await Drafter(handler, logger, logPrompts: false).DraftNarrativeAsync(Ctx);

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("refusal");
        log.Should().Contain("textLength=10");
        log.Should().NotContain("拒否された散文の断片");
    }

    // IADR-0104 決定5: 上限到達は劣化であり拒否ではない。本文は破棄しない（IADR-0101 の劣化観測を壊さない）。
    [Fact]
    public async Task 上限到達_max_tokens_は本文を破棄せず返し_劣化を警告ログに残す()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"text":"本日は堅調でした。","model":"claude","sent":true,"stopReason":"max_tokens"}""");

        var text = await Drafter(handler, logger, logPrompts: false).DraftNarrativeAsync(Ctx);

        text.Should().Be("本日は堅調でした。");
        string.Join("\n", logger.Messages).Should().Contain("max_tokens");
    }

    // 非破壊: stopReason 未設定（上流未更新）・未知値・正常終了は現行挙動のまま本文を返す。
    [Theory]
    [InlineData("""{"text":"散文","sent":true}""")]
    [InlineData("""{"text":"散文","sent":true,"stopReason":null}""")]
    [InlineData("""{"text":"散文","sent":true,"stopReason":"end_turn"}""")]
    [InlineData("""{"text":"散文","sent":true,"stopReason":"future_reason"}""")]
    public async Task stopReason_欠落_未知値_正常終了は現行どおり本文を返す(string body)
    {
        (await Drafter(new StubHandler(HttpStatusCode.OK, body)).DraftNarrativeAsync(Ctx)).Should().Be("散文");
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

    // 遅延して**正常応答**を返す（打ち切られなかった場合に本文が返ることを観測するため）。
    private sealed class DelayingRespondingHandler(TimeSpan delay, string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
