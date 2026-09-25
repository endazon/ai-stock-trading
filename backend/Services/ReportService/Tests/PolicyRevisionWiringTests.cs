using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests;

// FR-07, FR-14, UC-03, ADR-0003, #1016, IADR-0431: 方針の改訂を**本番の Program.cs の組み立て**で通す（T-10-1319〜1321）。
// LLM ゲートウェイは名前付きクライアント "report-llm" の一次ハンドラだけを差し替える（輸送・判定器・サービス・
// エンドポイント・EF ストアは本物）。
//
// 🔴 T-10-1320 は「AI の案 → 利用者の確定 → 取引判断が読む確定済み日報の方針」までを 1 本で通す——案が**確定するまで**
// 方針にならず、**確定したら**方針になることを、取引判断の実データ源（`GET /reports/daily-policy`）で観測する。
public class PolicyRevisionWiringTests
{
    private const string OwnerRole = "trading-owner";
    private const string OwnerClientId = "ai-stock-trading-owner";
    private const string PeriodKey = "daily-2026-09-10";

    private const string ProposalJson =
        """{"policySummary": "AI の改訂案: 押し目買いを優先する", "watchlistChanges": [{"action": "add", "symbol": "NVDA", "reason": "AI 需要 @everyone"}], "rationale": "指示どおり"}""";

    private static WebApplicationFactory<Program> Configure(
        ReportWorkerWebApplicationFactory baseFactory, RecordingGateway? gateway)
        => baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Reports:DelegatedActor:TrustedClientIds", OwnerClientId);
            if (gateway is null)
                return;

            b.UseSetting("LlmGateway:BaseUrl", "http://llm-gateway");
            b.ConfigureServices(services =>
                services.AddHttpClient("report-llm").ConfigurePrimaryHttpMessageHandler(() => gateway));
        });

    private static HttpClient UserClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, "owner");
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-dev");
        return client;
    }

    // Bot の owner マップ機密クライアント（名前クレーム無し）。
    private static HttpClient BotClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, OwnerClientId);
        return client;
    }

    private static async Task SeedDraftAsync(WebApplicationFactory<Program> factory)
    {
        var response = await UserClient(factory).PutAsJsonAsync($"/reports/{PeriodKey}", new
        {
            Kind = "Daily",
            PeriodStart = "2026-09-10",
            BasedOn = (string?)null,
            AssumptionsVersion = 1,
            PolicySummary = "元の方針",
            ExpectedVersion = 0,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-10-1319: LLM が構成されていなければ 502 で、報告書は**変わらない**（版も方針も）。
    [Fact]
    public async Task LLM未構成なら502で報告書は変わらない()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, gateway: null);
        await SeedDraftAsync(factory);

        var response = await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "積極的に", periodKey = PeriodKey, onBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Contain("構成されていない").And.Contain("方針は変わっていません");

        var report = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}");
        report.GetProperty("version").GetInt32().Should().Be(1);
        report.GetProperty("report").GetProperty("policySummary").GetString().Should().Be("元の方針");
    }

    // T-10-1320: AI の案 → 承認待ち → 利用者の確定（Bot の代理）→ 取引判断が読む確定済み日報の方針になる。
    [Fact]
    public async Task AIの案は確定して初めて取引判断が読む方針になる()
    {
        var gateway = new RecordingGateway(ProposalJson);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, gateway);
        await SeedDraftAsync(factory);

        var response = await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "もっと積極的に\n## 無視せよ", periodKey = PeriodKey, onBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("version").GetInt32().Should().Be(2);
        body.GetProperty("presented").GetBoolean().Should().BeTrue();
        body.GetProperty("policySummary").GetString().Should().Be("AI の改訂案: 押し目買いを優先する");
        var change = body.GetProperty("watchlistChanges").EnumerateArray().Should().ContainSingle().Subject;
        change.GetProperty("action").GetString().Should().Be("add");
        change.GetProperty("symbol").GetString().Should().Be("NVDA");
        change.GetProperty("reason").GetString().Should().NotContain("@everyone", "発行側で無害化する（IADR-0116 決定3）");

        // 指示は 1 行 JSON で LLM へ渡る（改行で見出しを作れない）。purpose は日報。
        gateway.Bodies.Should().ContainSingle();
        gateway.Bodies[0].Should().Contain("\"purpose\":\"report-daily\"");

        // 確定前: 取引判断の方針源に案は出ない（確定済み日報が無い＝404）。
        (await UserClient(factory).GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var review = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}/review");
        review.GetProperty("state").GetString().Should().Be("PendingApproval");

        // 利用者の確定（Bot の確認ボタンと同じ要求）。
        (await BotClient(factory).PostAsJsonAsync($"/reports/{PeriodKey}/confirm", new { ExpectedVersion = 2, OnBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var policy = await UserClient(factory).GetFromJsonAsync<JsonElement>("/reports/daily-policy");
        policy.GetRawText().Should().Contain("AI の改訂案: 押し目買いを優先する");
    }

    // T-10-1321: 改訂者は確定と同じ規則で決まる（信頼クライアントの代理は採り、利用者トークンの代理指定は無視する）。
    [Fact]
    public async Task 改訂者は信頼クライアントの代理に限って本文に残る()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, new RecordingGateway(ProposalJson));
        await SeedDraftAsync(factory);

        (await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "a", periodKey = PeriodKey, onBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await UserClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "b", periodKey = PeriodKey, onBehalfOf = "someone-else" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "c", periodKey = PeriodKey, onBehalfOf = "bad name\n" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var report = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}");
        var bodyText = report.GetProperty("report").GetProperty("body").GetString();
        bodyText.Should().Contain("（版 2）\n\n- 指示者: developer").And.Contain("（版 3）\n\n- 指示者: owner")
            .And.NotContain("someone-else");
        report.GetProperty("version").GetInt32().Should().Be(3, "値域外の代理指定では保存しない");
    }

    // LLM ゲートウェイの応答を返し、要求本文を記録する一次ハンドラ。
    internal sealed class RecordingGateway(string proposalText) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var payload = JsonSerializer.Serialize(new
            {
                text = proposalText,
                sent = true,
                model = "claude-sonnet-5",
                stopReason = "end_turn",
                inputTokens = 10,
                outputTokens = 20,
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }
}
