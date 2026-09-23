using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using AiStockTrading.Shared.Contracts.Logging;
using ReportService.Features.Reports;
using ReportService.Domain;
using ReportService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ReportService.Tests;

// FR-06/16, IADR-0071 決定1: platform LLM ゲートウェイ POST /complete を呼ぶ実 LLM 散文ドラフトの写像とフェイルセーフを
// fake HttpMessageHandler で検証する（実ネットワーク不使用）。送信拒否/失敗/タイムアウト/空応答はプレースホルダ散文へ倒す。
// 数値には一切関与しない（数値はコード集計が権威・FR-16）。
public class HttpReportNarrativeDrafterTests
{
    private static readonly ReportNarrativeContext Ctx = new(
        ReportKind.Daily, "daily-2026-07-18", "2026-07-18", ["JP"],
        new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌日は継続");

    // 期間表記は自然キー（ReportPeriod.ExpectedKey）そのもの。定数へ括り出すのは可読性のためと、
    // `PeriodKey = "weekly-…"` の形が gitleaks の generic-api-key（キーワード "key" ＋エントロピー）に
    // 誤検知されるためである（値は認証情報ではない。ReportPolicyDraftTests と同じ扱い）。
    // #900: 打ち切りが効かなくなったときに、黙って固まる代わりに理由付きで赤くするための上限
    //（合否の基準ではない。合否は下の順序が決める）。
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private const string WeeklyPeriod = "weekly-2026-W31";
    private const string MonthlyPeriod = "monthly-2026-07";

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

    // NFR-05, #724, IADR-0323: 🔴 **否定形。** 認可の失敗（401/403）でも倒れ先はプレースホルダのままだが、
    // 記録は「モデルが使えない」と読める形にしない。基盤が LLM ゲートウェイの REST 面へ `ServiceCaller` を
    // 掛けた（MSP#1364）とき、原因を取り違えた記録が残ると、次に読む人が LLM 提供側を疑うことになる。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]  // 401: 資格情報が無い・失効
    [InlineData(HttpStatusCode.Forbidden)]     // 403: 認証は通ったがロールが足りない
    public async Task 認可の失敗は資格情報を名指しし_モデルの可否として記録しない(HttpStatusCode status)
    {
        var logger = new RecordingLogger();

        var text = await Drafter(new StubHandler(status, ""), logger).DraftNarrativeAsync(Ctx);

        // ① 倒れ先は不変（安全側＝捏造しない定型散文）。
        text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        // ② 原因を名指しする（運用者が見る場所を誤らせない）。
        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("認可");
        log.Should().Contain("LlmGateway:Auth");
        // ③ 🔴 モデルの可否として記録しない。
        log.Should().NotContain("モデル不可");
    }

    // 対照群: 認可以外の非 2xx は従来どおりの汎用メッセージ（上の否定形が「常に認可と書く」実装で緑にならない）。
    [Fact]
    public async Task 認可以外の非_2xx_は従来どおりの記録のまま()
    {
        var logger = new RecordingLogger();

        await Drafter(new StubHandler(HttpStatusCode.InternalServerError, ""), logger).DraftNarrativeAsync(Ctx);

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("非 2xx");
        log.Should().NotContain("認可");
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
    // プレースホルダへ縮退していた。同一インスタンス・同一のハンドラで、日報は打ち切られ週報は通ることを固定する。
    //
    // #900, IADR-0366: 合否を「100 ms の打ち切り」対「600 ms の応答遅延」という**壁時計どうしの競争**で決めていた
    // ため、全ソリューション実行で稀に遅延が勝ち、日報が応答を受け取って落ちた（実測の機序は IADR-0366）。
    // 遅延を同期点（`TaskCompletionSource`）へ置き換え、**順序**で同じ命題を固定する:
    //   ① 週報を飛行中にする → ② 同じハンドラのまま日報を投げ、**解放しないのに戻ってくる**ことで打ち切りを観測する
    //   → ③ その時点でも週報は打ち切られていない（種別ごとに上限が違う）→ ④ 解放すると週報は本文を受け取る。
    // 週報が「日報の上限より長く飛んでいた」ことは②の完了が保証する（時刻の比較ではなく前後関係で言える）。
    [Fact]
    public async Task タイムアウトは報告書種別ごとに効く_日報は打ち切られ週報は通る()
    {
        var handler = new HeldRespondingHandler("""{"text":"週次の所感です。","sent":true}""");
        // HttpClient 自体の上限は外し（無期限）、**種別ごとの打ち切りだけ**が要求を切れるようにする。
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://llm-gateway"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var drafter = new HttpReportNarrativeDrafter(
            http, NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", null, logPrompts: false,
            // 週報の上限は Guard（30 秒）より長く採る（#906 監査）。20 秒だと、20〜30 秒の停止で週報のトークンが
            // 先に落ち、「種別ごとの打ち切りが壊れている」という誤った失敗になる。長く採れば停止は Guard の
            // タイムアウト（原因が分かる形）で落ちる。判別力は変わらない（単一の上限へ潰すと依然として赤）。
            timeoutFor: kind => kind == ReportKind.Daily ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(5));

        // ① 週報を飛行中にする（ハンドラは解放されるまで応答しない）。
        var weeklyDraft = drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Weekly, PeriodKey = WeeklyPeriod });
        var weeklyRequest = await handler.NextRequestAsync(Guard);

        // ② 日報は解放しない。戻ってきたなら、戻した経路は種別ごとの打ち切りしかない。
        var dailyDraft = drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Daily });
        var dailyRequest = await handler.NextRequestAsync(Guard);
        (await dailyDraft.WaitAsync(Guard)).Should().Be(ReportNarrativeDefaults.PlaceholderText);
        dailyRequest.Token.IsCancellationRequested.Should().BeTrue("日報は打ち切りで切られる（応答は返っていない）");

        // ③ 週報は日報より前から飛んでおり、日報の上限はもう発火した。それでも週報は切られていない。
        weeklyRequest.Token.IsCancellationRequested.Should().BeFalse("週報の上限は日報より長い（種別ごとに効く）");

        // ④ 解放すれば週報は本文を受け取る（打ち切りではなく応答で終わる）。
        weeklyRequest.Release();
        (await weeklyDraft.WaitAsync(Guard)).Should().Be("週次の所感です。");
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

        await drafter.DraftNarrativeAsync(Ctx with { Kind = ReportKind.Monthly, PeriodKey = MonthlyPeriod });

        var log = string.Join("\n", logger.Messages);
        log.Should().Contain("タイムアウト");
        log.Should().Contain("Monthly");
        log.Should().Contain("0.5");
    }

    // T-7: 呼び出し側のキャンセル（停止要求）はタイムアウトと取り違えず、そのまま伝播する（縮退で握り潰さない）。
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

    // --- NFR, #708, IADR-0316: ログ偽装（CWE-117）の防止。全量記録は保ったまま行を割らせない ------------

    // 攻撃の形: 外部由来の文字列へ改行と「それらしい偽のログ行」を仕込む。ESC は端末表示を、
    // U+2028 は JSON を読むログビューアの行分割を壊す。
    private const string ForgedTail = "2026-07-18 09:00:00 [INF] 取引ガードを解除しました";
    private static readonly string ForgedText = $"正常な散文\r\n{ForgedTail}\u001b[31m\u2028末尾\t\u0085";

    private static readonly ReportNarrativeContext ForgedCtx = Ctx with { PolicySummary = ForgedText };

    [Fact]
    public async Task LogPrompts有効時_プロンプト中の制御文字はログ行を割らない()
    {
        var logger = new RecordingLogger();
        var handler = new StubHandler(HttpStatusCode.OK, """{"text":"堅調でした。","model":"claude","sent":true}""");

        await Drafter(handler, logger, logPrompts: true).DraftNarrativeAsync(ForgedCtx);

        var promptLog = logger.Messages.Single(m => m.Contains("報告書散文 LLM 要求"));
        ContainsControlCharacters(promptLog).Should().BeFalse("ログ 1 レコードは 1 行でなければならない");
        // 🔴「そもそも書かない」で逃げていないこと: 本文は識別できる形で残る。
        promptLog.Should().Contain("正常な散文");
        promptLog.Should().Contain(ForgedTail);
    }

    [Fact]
    public async Task LogPrompts有効時_生出力中の制御文字はログ行を割らない()
    {
        var logger = new RecordingLogger();
        // 生出力（LLM が返す本文）に同じ細工を入れる。JSON エスケープで渡すため実体は制御文字である。
        var handler = new StubHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { text = ForgedText, model = "claude", sent = true }));

        await Drafter(handler, logger, logPrompts: true).DraftNarrativeAsync(Ctx);

        var responseLog = logger.Messages.Single(m => m.Contains("報告書散文 LLM 応答"));
        ContainsControlCharacters(responseLog).Should().BeFalse();
        responseLog.Should().Contain("正常な散文");
        responseLog.Should().Contain(ForgedTail);
    }

    [Fact]
    public async Task 生出力が上限を超えるとログでは切られ落とした文字数が明示される()
    {
        var logger = new RecordingLogger();
        var body = JsonSerializer.Serialize(new
        {
            text = new string('あ', LogSanitizer.DefaultMaxLength + 25),
            model = "claude",
            sent = true,
        });

        await Drafter(new StubHandler(HttpStatusCode.OK, body), logger, logPrompts: true).DraftNarrativeAsync(Ctx);

        logger.Messages.Single(m => m.Contains("報告書散文 LLM 応答"))
            .Should().Contain("…(truncated 25 chars)");
    }

    // 🔴 **陽性対照。** 正規化を通さずに同じ値を同じ土台（RecordingLogger）へ書けば、
    // ログ行は実際に割れる。これが無いと、上の 2 件は「値をログへ書かない実装」でも緑になる。
    [Fact]
    public void 陽性対照_正規化を通さなければ同じ値がログ行を割る()
    {
        var logger = new RecordingLogger();

        logger.LogInformation("報告書散文 LLM 応答: text={Text}", ForgedText);

        var raw = logger.Messages.Single();
        ContainsControlCharacters(raw).Should().BeTrue("素通しなら制御文字がログ行へ入る");
        raw.Should().Contain("\n");
        raw.Split('\n').Should().HaveCountGreaterThan(1, "行が割れて偽のログ行が生まれる");
    }

    // 行を割り得る文字（制御文字＋ U+2028 / U+2029）が 1 つでも残っていれば true。
    private static bool ContainsControlCharacters(string text) =>
        text.Any(ch => char.IsControl(ch) || ch is '\u2028' or '\u2029');

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

    // #900, IADR-0366: 要求を**テストが解放するまで保持する**ハンドラ。時間では応答せず、解放か打ち切りでしか
    // 終わらないため、「打ち切りが効いたか」を実時間の競争ではなく順序で観測できる。
    private sealed class HeldRespondingHandler(string body) : HttpMessageHandler
    {
        private readonly Channel<HeldRequest> _arrivals = Channel.CreateUnbounded<HeldRequest>();

        // 到達した要求を 1 件受け取る（来なければ上限で失敗する＝黙って固まらない）。
        public async Task<HeldRequest> NextRequestAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _arrivals.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var held = new HeldRequest(cancellationToken);
            await _arrivals.Writer.WriteAsync(held, CancellationToken.None).ConfigureAwait(false);
            await held.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    // 保持中の 1 要求。`Token` は製品が要求ごとに張る打ち切り用トークンで、発火の有無をテストが観測する。
    private sealed class HeldRequest(CancellationToken token)
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token { get; } = token;
        public Task Released => _released.Task;
        public void Release() => _released.TrySetResult();
    }
}
