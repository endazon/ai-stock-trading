using System.Net;
using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using AiStockTrading.Report.Infrastructure.Foundation.Adapters;
using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Report.Infrastructure.Tests;

// FR-06, FR-16, ADR-0015, ADR-0017 決定4, #335, #347, IADR-0217/0219:
// 報告書生成における **①実効モデルの記録（報告書メタ）**・**②フォールバック発火の通知**・
// **③費用の計上（月報の利用実績）** の 3 経路と、禁止モデルの否定形。
//
// 🔴 ADR-0017 決定4 の目的は「**沈黙のフォールバックを作らない**」ことである。
// 「月報が第 1 候補で書かれたのか第 2 候補で書かれたのかは、その月報を次の 1 か月の方針書として
// 採用する際の判断材料である。」
public class HttpReportNarrativeDrafterVisibilityTests
{
    private static ReportNarrativeContext Ctx(ReportKind kind) => kind switch
    {
        ReportKind.Monthly => new(kind, "monthly-2026-07", "2026-07", ["JP"],
            new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌月は継続"),
        ReportKind.Weekly => new(kind, "weekly-2026-W31", "2026-W31", ["JP"],
            new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌週は継続"),
        _ => new(kind, "daily-2026-07-18", "2026-07-18", ["JP"],
            new PnlSummary(1m, 0m, 0m, 1m, 0m, 1, 1, 1), "翌日は継続"),
    };

    // purposeOverride は null＝種別ごとの purpose（report-daily / weekly / monthly）を送る本番構成。
    private static HttpReportNarrativeDrafter Drafter(
        HttpMessageHandler handler,
        RecordingUsageReporter? usage = null,
        RecordingGovernanceReporter? governance = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://llm-gateway") },
            NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", purposeOverride: null,
            logPrompts: false, timeoutFor: null,
            usageReporter: usage ?? new RecordingUsageReporter(),
            governanceReporter: governance ?? new RecordingGovernanceReporter());

    private static string Body(string model, string text = "本日は堅調な地合いでした。", int input = 1200, int output = 350) =>
        $$"""{"text":"{{text}}","model":"{{model}}","sent":true,"inputTokens":{{input}},"outputTokens":{{output}}}""";

    // ---- ①報告書メタ: 実効モデルを記録する ---------------------------------------------------

    [Theory]
    [InlineData(ReportKind.Monthly, "report-monthly", "claude-opus-5")]
    [InlineData(ReportKind.Weekly, "report-weekly", "claude-opus-5")]
    [InlineData(ReportKind.Daily, "report-daily", "claude-sonnet-5")]
    public async Task 第1候補で生成されたらメタ情報に_Primary_として記録する(
        ReportKind kind, string expectedPurpose, string pin)
    {
        var draft = await Drafter(new StubHandler(HttpStatusCode.OK, Body(pin))).DraftAsync(Ctx(kind));

        draft.ModelUsage.Should().NotBeNull();
        draft.ModelUsage!.Purpose.Should().Be(expectedPurpose);
        draft.ModelUsage.ExpectedModel.Should().Be(pin);
        draft.ModelUsage.EffectiveModel.Should().Be(pin);
        draft.ModelUsage.IsPrimary.Should().BeTrue();
    }

    // ADR-0017 決定4-(1): **フォールバック発火時はその事実も記録する。**
    [Theory]
    [InlineData(ReportKind.Monthly, "claude-opus-5", "claude-sonnet-5")]
    [InlineData(ReportKind.Weekly, "claude-opus-5", "claude-sonnet-5")]
    [InlineData(ReportKind.Daily, "claude-sonnet-5", "claude-haiku-4-5")]
    public async Task 第2候補で生成されたらメタ情報に_FallbackFired_として記録する(
        ReportKind kind, string pin, string fallback)
    {
        var draft = await Drafter(new StubHandler(HttpStatusCode.OK, Body(fallback))).DraftAsync(Ctx(kind));

        draft.ModelUsage!.ExpectedModel.Should().Be(pin);
        draft.ModelUsage.EffectiveModel.Should().Be(fallback);
        draft.ModelUsage.Outcome.Should().Be(nameof(LlmAssignmentOutcome.FallbackFired));
        draft.ModelUsage.IsPrimary.Should().BeFalse();
        // 報告書はフォールバックを許すため、本文は成果物として採る（ADR-0017 §理由）。
        draft.Text.Should().Be("本日は堅調な地合いでした。");
    }

    // 🔴 **null（未供給）を「フォールバックなし」へ潰さない。**
    // 縮退でプレースホルダへ倒れたときはモデルを知り得ないため、メタは null のままである。
    [Fact]
    public async Task 縮退時はモデルを名乗らせず_メタ情報を_null_のままにする()
    {
        var draft = await Drafter(new StubHandler(HttpStatusCode.InternalServerError, "")).DraftAsync(Ctx(ReportKind.Daily));

        draft.Text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        draft.ModelUsage.Should().BeNull();
    }

    // ---- ②警告通知: フォールバック発火を publish 経路へ流す ----------------------------------

    [Fact]
    public async Task フォールバック発火は通知経路へ流す()
    {
        var governance = new RecordingGovernanceReporter();

        await Drafter(new StubHandler(HttpStatusCode.OK, Body("claude-sonnet-5")), governance: governance)
            .DraftAsync(Ctx(ReportKind.Monthly));

        var fired = governance.Fired.Should().ContainSingle().Subject;
        fired.Purpose.Should().Be("report-monthly");
        fired.Evaluation.Outcome.Should().Be(LlmAssignmentOutcome.FallbackFired);
        fired.Evaluation.ExpectedModel.Should().Be("claude-opus-5");
    }

    // 基盤で用途エントリが未登録・ZDR 除外だと LlmRouter は無音で DefaultModel へ落ちる（platform IADR-0102）。
    // **その落下も「割当どおりでない」として通知する** —— 沈黙させないことが決定 4 の目的である。
    [Fact]
    public async Task 割当表に無いモデルへ落ちた場合も通知する()
    {
        var governance = new RecordingGovernanceReporter();

        var draft = await Drafter(new StubHandler(HttpStatusCode.OK, Body("claude-opus-4-8")), governance: governance)
            .DraftAsync(Ctx(ReportKind.Daily));

        governance.Fired.Should().ContainSingle()
            .Which.Evaluation.Outcome.Should().Be(LlmAssignmentOutcome.Unassigned);
        // 報告書は発注を伴わないため本文は採る（構成ドリフトのたびに方針階層を途切れさせない）。
        draft.Text.Should().Be("本日は堅調な地合いでした。");
    }

    // 🔴 **否定形**: 第 1 候補どおりなら通知しない（「常に通知する」実装でも緑になるのを防ぐ）。
    [Fact]
    public async Task 第1候補どおりなら通知しない()
    {
        var governance = new RecordingGovernanceReporter();

        await Drafter(new StubHandler(HttpStatusCode.OK, Body("claude-sonnet-5")), governance: governance)
            .DraftAsync(Ctx(ReportKind.Daily));

        governance.Fired.Should().BeEmpty();
    }

    // ---- 🔴 否定形: 禁止モデル（fable-5）の出力は成果物にしない -------------------------------

    // ADR-0015: `claude-fable-5` は ZDR 非対応であり本システムで使用しない。
    [Fact]
    public async Task 禁止モデルで生成された散文は破棄しプレースホルダへ倒す()
    {
        var governance = new RecordingGovernanceReporter();

        var draft = await Drafter(
            new StubHandler(HttpStatusCode.OK, Body(LlmAssignments.ForbiddenModel, "禁止モデルの出力")),
            governance: governance).DraftAsync(Ctx(ReportKind.Monthly));

        draft.Text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        draft.Text.Should().NotContain("禁止モデルの出力");
        draft.ModelUsage!.Outcome.Should().Be(nameof(LlmAssignmentOutcome.Forbidden));
        governance.Fired.Should().ContainSingle();
    }

    // 🔴 **否定形**: 要求本文に禁止モデルを名指ししない（モデルの決定権は基盤の LlmRouter に残す）。
    [Fact]
    public async Task 要求本文に禁止モデルを載せない()
    {
        var handler = new CapturingHandler(Body("claude-sonnet-5"));

        await Drafter(handler).DraftAsync(Ctx(ReportKind.Daily));

        handler.LastBody.Should().NotContain(LlmAssignments.ForbiddenModel);
    }

    // ---- ③費用: 用途つきで計上する（月報の実績・#282 の是正） ---------------------------------

    [Theory]
    [InlineData(ReportKind.Monthly, "report-monthly")]
    [InlineData(ReportKind.Weekly, "report-weekly")]
    [InlineData(ReportKind.Daily, "report-daily")]
    public async Task 報告書生成の費用は用途つきで計上する(ReportKind kind, string expectedPurpose)
    {
        var usage = new RecordingUsageReporter();

        await Drafter(new StubHandler(HttpStatusCode.OK, Body("claude-opus-5")), usage).DraftAsync(Ctx(kind));

        var reported = usage.Calls.Should().ContainSingle().Subject;
        reported.Purpose.Should().Be(expectedPurpose);
        reported.InputTokens.Should().Be(1200);
        reported.OutputTokens.Should().Be(350);
        // IADR-0122 決定1: 単価解決の根拠は**応答が名乗った実効モデル**である。
        reported.Model.Should().Be("claude-opus-5");
        // 🔴 用途は上限の対象外側でなければならない（同じカウンタに積むと日報確定が止まる連鎖が生じる）。
        LlmCostScope.IsGoverned(reported.Purpose).Should().BeFalse();
    }

    // 送信が成立していない（Sent=false・非 2xx）なら課金は発生していないので計測しない。
    [Fact]
    public async Task 送信不可なら費用を計上しない()
    {
        var usage = new RecordingUsageReporter();

        await Drafter(
            new StubHandler(HttpStatusCode.OK, """{"text":"送信できません","model":"","sent":false}"""), usage)
            .DraftAsync(Ctx(ReportKind.Daily));

        usage.Calls.Should().BeEmpty();
    }

    // 計測は best-effort（IADR-0055）。失敗しても散文は返る。
    [Fact]
    public async Task 費用計測の失敗は散文生成を壊さない()
    {
        var drafter = new HttpReportNarrativeDrafter(
            new HttpClient(new StubHandler(HttpStatusCode.OK, Body("claude-sonnet-5")))
            {
                BaseAddress = new Uri("http://llm-gateway"),
            },
            NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", purposeOverride: null,
            logPrompts: false, timeoutFor: null, usageReporter: new ThrowingUsageReporter());

        (await drafter.DraftAsync(Ctx(ReportKind.Daily))).Text.Should().Be("本日は堅調な地合いでした。");
    }

    // 🔴 可視化も best-effort（決定4 の但し書き）。**発火の記録に失敗しても報告書生成は壊さない。**
    // 逆向き（記録できないなら散文も落とす）にすると、通知経路の一時障害が月次の方針書を止める。
    [Fact]
    public async Task 発火の記録に失敗しても散文生成は壊さない()
    {
        var drafter = new HttpReportNarrativeDrafter(
            new HttpClient(new StubHandler(HttpStatusCode.OK, Body("claude-sonnet-5")))
            {
                BaseAddress = new Uri("http://llm-gateway"),
            },
            NullLogger<HttpReportNarrativeDrafter>.Instance, "internal", purposeOverride: null,
            logPrompts: false, timeoutFor: null,
            usageReporter: new RecordingUsageReporter(),
            governanceReporter: new ThrowingGovernanceReporter());

        // 月報の第 1 候補は opus-5 なので、この応答は発火扱い＝記録経路を必ず通る。
        var draft = await drafter.DraftAsync(Ctx(ReportKind.Monthly));

        draft.Text.Should().Be("本日は堅調な地合いでした。");
        // 記録が失敗しても①メタ情報は残る（3 経路は互いに独立している）。
        draft.ModelUsage!.Outcome.Should().Be(nameof(LlmAssignmentOutcome.FallbackFired));
    }

    // ---- 縮退の理由ごとにメタ情報の残り方が変わる（#247, IADR-0104 決定3） ---------------------

    // 🔴 **応答が空（JSON null）ならモデルを知り得ない**——メタは null のままにする。
    // ここで「第 1 候補で書かれた」と読める値を作ると、沈黙のフォールバックそのものになる。
    [Fact]
    public async Task 応答が_JSON_null_ならプレースホルダへ倒しメタ情報も残さない()
    {
        var governance = new RecordingGovernanceReporter();

        var draft = await Drafter(new StubHandler(HttpStatusCode.OK, "null"), governance: governance)
            .DraftAsync(Ctx(ReportKind.Daily));

        draft.Text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        draft.ModelUsage.Should().BeNull();
        governance.Fired.Should().BeEmpty("名乗りが無い応答を割当逸脱として数えない");
    }

    // 🔴 **対になる否定形。** 本文が空でも**モデルは名乗られている**ため、メタ情報は残す。
    // 本文の可否とモデルの記録は別の関心事であり、まとめて捨てると月報の判断材料が欠ける。
    [Fact]
    public async Task 応答本文が空でもモデルを名乗っていればメタ情報は残す()
    {
        var draft = await Drafter(
            new StubHandler(HttpStatusCode.OK, """{"text":"   ","model":"claude-sonnet-5","sent":true}"""))
            .DraftAsync(Ctx(ReportKind.Daily));

        draft.Text.Should().Be(ReportNarrativeDefaults.PlaceholderText);
        draft.ModelUsage!.EffectiveModel.Should().Be("claude-sonnet-5");
        draft.ModelUsage.IsPrimary.Should().BeTrue("日報の第 1 候補どおりに応答している");
    }

    // ---- 非破壊の確認 -----------------------------------------------------------------------

    // 既定実装（DraftAsync）を持たない fake は従来どおり動く（既定インターフェース実装が委譲する）。
    [Fact]
    public async Task モデル情報を持たない実装は既定実装で従来どおり動く()
    {
        IReportNarrativeDrafter legacy = new LegacyDrafter();

        var draft = await legacy.DraftAsync(Ctx(ReportKind.Daily));

        draft.Text.Should().Be("旧実装の散文");
        draft.ModelUsage.Should().BeNull();
    }

    // ---- fake -------------------------------------------------------------------------------

    private sealed class LegacyDrafter : IReportNarrativeDrafter
    {
        public Task<string> DraftNarrativeAsync(ReportNarrativeContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult("旧実装の散文");
    }

    private sealed class RecordingUsageReporter : ILlmUsageReporter
    {
        public List<LlmUsage> Calls { get; } = [];

        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
        {
            Calls.Add(usage);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingUsageReporter : ILlmUsageReporter
    {
        public Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("計測の発行に失敗");
    }

    private sealed class RecordingGovernanceReporter : ILlmGovernanceReporter
    {
        public List<(LlmAssignmentEvaluation Evaluation, string Purpose)> Fired { get; } = [];

        public Task FallbackFiredAsync(
            LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default)
        {
            Fired.Add((evaluation, purpose));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGovernanceReporter : ILlmGovernanceReporter
    {
        public Task FallbackFiredAsync(
            LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("発火の記録に失敗");
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
