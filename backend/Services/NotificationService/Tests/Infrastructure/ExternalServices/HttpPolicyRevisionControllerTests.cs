extern alias ReportWorker;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationService.Infrastructure.ExternalServices;
using Xunit;
using ReportDomain = ReportWorker::ReportService.Domain;
using ReportRevise = ReportWorker::ReportService.Features.Reports.RevisePolicy;

namespace NotificationService.Tests;

// FR-07, FR-14, UC-03〜05, #1016, IADR-0431: 報告書サービスの方針の改訂の呼び出し（T-10-1329〜1333）。
// 🔴 **原則 A**: 200＝案あり／4xx・5xx＝案なし（保存されていない）／タイムアウト・例外・解釈不能＝**不明**（保存された可能性）。
// T-10-1332・1333 は越境の契約（IADR-0420）: 送り手（報告書サービス）の**本物の型**を送り手の JSON 設定で直列化・逆直列化する。
public class HttpPolicyRevisionControllerTests
{
    // 報告書サービスの HTTP JSON 設定（web 既定＋文字列列挙。Program.cs の ConfigureHttpJsonOptions）。
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static HttpPolicyRevisionController Controller(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://report-service") }, NullLogger<HttpPolicyRevisionController>.Instance);

    private static ReportRevise.PolicyRevisionResponse SenderResponse() => new(
        "daily-2026-09-27", 2, Created: false, Presented: true,
        "方針の改訂案を保存し、承認待ちにしました（確定するまで取引には適用されません）。",
        "押し目買いを優先する",
        [new ReportRevise.WatchlistChangeView("add", "NVDA", "AI 需要")],
        "指示どおり");

    // T-10-1329: 4xx / 5xx は案なし。報告書サービスの `error` をそのまま見せる。
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task 非2xxは案なしでerrorを見せる(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, """{"error":"AI の案を作れませんでした（x）。方針は変わっていません。"}""");

        var outcome = await Controller(handler).ReviseAsync(null, "指示", "developer", null);

        (outcome.Succeeded, outcome.Indeterminate).Should().Be((false, false));
        outcome.Message.Should().Be("AI の案を作れませんでした（x）。方針は変わっていません。");
        outcome.Proposal.Should().BeNull();
    }

    [Fact]
    public async Task 本文の無い非2xxは状態番号で案なしを伝える()
    {
        var outcome = await Controller(new FakeHandler(HttpStatusCode.Forbidden, "")).ReviseAsync(null, "指示", "developer", null);

        outcome.Succeeded.Should().BeFalse();
        outcome.Indeterminate.Should().BeFalse();
        outcome.Message.Should().Contain("HTTP 403").And.Contain("trading-owner");
    }

    // T-10-1330: タイムアウト・例外は**不明**（保存された可能性があると伝える）。呼び出し側の取り消しは伝播する。
    [Fact]
    public async Task タイムアウトと例外は不明として伝える()
    {
        var timeout = await Controller(new ThrowingHandler(new TaskCanceledException("timeout"))).ReviseAsync(null, "指示", "developer", null);
        (timeout.Succeeded, timeout.Indeterminate).Should().Be((false, true));
        timeout.Message.Should().Contain("/report show");

        var broken = await Controller(new ThrowingHandler(new HttpRequestException("reset"))).ReviseAsync(null, "指示", "developer", null);
        broken.Indeterminate.Should().BeTrue();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = () => Controller(new ThrowingHandler(new TaskCanceledException())).ReviseAsync(null, "指示", "developer", null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-10-1331: 2xx だが解釈できない応答は不明（200 は保存の後に返るため「失敗」とは言わない）。
    [Theory]
    [InlineData("null")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"periodKey":"daily-2026-09-27","version":0,"policySummary":"p"}""")]
    public async Task 解釈できない2xxは不明(string body)
    {
        var outcome = await Controller(new FakeHandler(HttpStatusCode.OK, body)).ReviseAsync(null, "指示", "developer", null);

        (outcome.Succeeded, outcome.Indeterminate).Should().Be((false, true));
    }

    // T-10-1332: 送り手の本物の応答型（PolicyRevisionResponse）を送り手の設定で直列化した本文から、案を読める。
    [Fact]
    public async Task 送り手の本物の応答型から案を読める()
    {
        ReportRevise.PolicyRevisionResponse sent = SenderResponse();
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(sent, ReportWire));

        var outcome = await Controller(handler).ReviseAsync("daily-2026-09-27", "指示", "developer", null);

        outcome.Succeeded.Should().BeTrue();
        var proposal = outcome.Proposal!;
        (proposal.PeriodKey, proposal.Version, proposal.Presented, proposal.Created).Should().Be(("daily-2026-09-27", 2, true, false));
        proposal.PolicySummary.Should().Be("押し目買いを優先する");
        proposal.WatchlistChanges.Should().ContainSingle().Which.Should().Be(
            new NotificationService.Features.Notifications.WatchlistChangeSuggestionView("add", "NVDA", "AI 需要"));
        proposal.Rationale.Should().Be("指示どおり");
    }

    // T-10-1333: 受け手が送る要求本文を、送り手の本物の要求型（RevisePolicyRequest）が読める（項目名の不一致で指示・代理が落ちない）。
    [Fact]
    public async Task 送る要求本文は送り手の本物の要求型で読める()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(SenderResponse(), ReportWire));

        await Controller(handler).ReviseAsync("daily-2026-09-27", "もっと積極的に", "developer", null);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.RequestUri.Should().Be("http://report-service/reports/policy-revisions");
        var request = JsonSerializer.Deserialize<ReportRevise.RevisePolicyRequest>(handler.Body!, ReportWire)!;
        request.Should().Be(new ReportRevise.RevisePolicyRequest("もっと積極的に", "daily-2026-09-27", "developer"));
    }

    // 🔴 T-10-1352（再監査 nit 1・ADR-0003）: **Discord に表示される方針から幅ゼロ空白を除くと、確定される（保存される）方針と
    // 完全に一致する**——分割された通をまたいでも。保存される方針は送り手の本物の検証（PolicyRevisionProposalParser）の結果、
    // 表示は送り手の本物の無害化（PolicyRevisionResponse.Display）→ 本物の応答型の直列化 → 受け手の解釈 → 分割の順に作る。
    // 3 行以上の改行・前後の空白・メンション・マスクリンク・上限ちょうどの長さを含める（旧実装は空行を畳み境界語を落としていた）。
    [Theory]
    [MemberData(nameof(StoredPolicies))]
    public async Task 表示される方針は幅ゼロ空白を除けば保存される方針と一致する(string llmPolicy)
    {
        var parsed = ReportDomain.PolicyRevisionProposalParser.Parse(
            JsonSerializer.Serialize(new { policySummary = llmPolicy, watchlistChanges = Array.Empty<object>() }));
        parsed.IsValid.Should().BeTrue(parsed.Reason);
        var stored = parsed.Proposal!.PolicySummary;

        ReportRevise.PolicyRevisionResponse sent = SenderResponse() with { PolicySummary = ReportRevise.PolicyRevisionResponse.Display(stored) };
        var outcome = await Controller(new FakeHandler(HttpStatusCode.OK, JsonSerializer.Serialize(sent, ReportWire)))
            .ReviseAsync("daily-2026-09-27", "指示", "developer", null);
        var proposal = outcome.Proposal!;
        var messages = NotificationService.Domain.PolicyRevisionMessage.Build(
            proposal.PeriodKey, proposal.Version, proposal.Presented, proposal.Created, proposal.Message,
            proposal.PolicySummary, [], proposal.Rationale);

        var displayed = string.Concat(messages
            .Where(m => m.StartsWith("【方針案 ", StringComparison.Ordinal))
            .Select(m => m[(m.IndexOf('\n') + 1)..]));
        displayed.Replace(ReportDomain.ReportSummarySanitizer.MentionBreaker, string.Empty, StringComparison.Ordinal)
            .Should().Be(stored);
    }

    public static TheoryData<string> StoredPolicies() => new()
    {
        "押し目買いを優先する",
        "  前後の空白\r\n\r\n\r\n\r\n3 行以上の空行\n\n\n\n末尾  ",
        "@everyone @here <@123> <#456> [公式発表](https://evil.example) 素の https://example.com",
        string.Concat(Enumerable.Range(0, 400).Select(_ => "@here")),
        new string('方', 1998) + "\n\n",
    };

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }
}
