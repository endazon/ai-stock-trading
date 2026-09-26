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

    // 投稿を壊し得る方針（一斉メンション・個別メンション・マスクリンク）と、上限ちょうどの長さの方針。
    private const string HostilePolicyJson =
        """{"policySummary": "@everyone 押し目買い <@123> [公式発表](https://evil.example) @here", "watchlistChanges": [], "rationale": "[説明](https://evil.example)"}""";

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

    // T-10-1524（FR-14, FR-09, #1039）: 確定済みの報告書への /policy は、本番の組み立てでも 409 のまま（意味は変えない）で、
    // `error` に改訂の手段（period を省略したときの対象・リスク設定画面）が載る。AI は呼ばれず、版も方針も変わらない。
    [Fact]
    public async Task 確定済みへの改訂は409のまま改訂の手段を返す()
    {
        var gateway = new RecordingGateway(ProposalJson);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, gateway);
        await SeedDraftAsync(factory);
        (await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "積極的に", periodKey = PeriodKey, onBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await BotClient(factory).PostAsJsonAsync($"/reports/{PeriodKey}/confirm", new { ExpectedVersion = 2, OnBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmedVersion = (await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}"))
            .GetProperty("version").GetInt32();

        var response = await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "もっと積極的に", periodKey = PeriodKey, onBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.Should().StartWith($"報告書 {PeriodKey} は確定済みのため改訂できません")
            .And.Contain("period を省略した /policy は当日（JST）の日報")
            .And.Contain("リスク設定画面");
        gateway.Bodies.Should().ContainSingle("確定済みへの改訂では AI を呼ばない");

        var report = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}");
        report.GetProperty("version").GetInt32().Should().Be(confirmedVersion, "確定時の版のまま");
        report.GetProperty("report").GetProperty("state").GetString().Should().Be("Confirmed");
        report.GetProperty("report").GetProperty("policySummary").GetString().Should().Be("AI の改訂案: 押し目買いを優先する");
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

    // T-10-1341（監査 BLOCKING 2）: サービス主体（trading-service だけのトークン）は方針の改訂を呼べない（403）。
    // 改訂は利用者のみ（ADR-0003）。登録を読み取りグループ（OwnerOrService）へ移すとこの試験が赤になる。
    [Fact]
    public async Task サービス主体は方針の改訂を呼べない()
    {
        var gateway = new RecordingGateway(ProposalJson);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, gateway);
        await SeedDraftAsync(factory);

        var service = factory.CreateClient();
        service.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        var response = await service.PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "積極的に", periodKey = PeriodKey });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        gateway.Bodies.Should().BeEmpty("LLM も呼ばない");
        var report = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}");
        report.GetProperty("version").GetInt32().Should().Be(1);
    }

    // T-10-1342: 方針・説明の一斉メンション・個別メンション・マスクリンクは投稿向けに崩して返す。保存する方針は原文のまま。
    [Fact]
    public async Task 方針のメンションとマスクリンクは投稿向けに崩して返す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, new RecordingGateway(HostilePolicyJson));
        await SeedDraftAsync(factory);

        var response = await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "a", periodKey = PeriodKey, onBehalfOf = "developer" });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var policy = body.GetProperty("policySummary").GetString()!;
        policy.Should().NotContain("@everyone").And.NotContain("@here").And.NotContain("<@").And.NotContain("](");
        policy.Replace(ReportService.Domain.ReportSummarySanitizer.MentionBreaker, string.Empty, StringComparison.Ordinal)
            .Should().Be("@everyone 押し目買い <@123> [公式発表](https://evil.example) @here", "読める内容は変えない（幅ゼロ空白だけ）");
        body.GetProperty("rationale").GetString().Should().NotContain("](");

        var report = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}");
        report.GetProperty("report").GetProperty("policySummary").GetString()
            .Should().Be("@everyone 押し目買い <@123> [公式発表](https://evil.example) @here", "保存するのは検証済みの原文");
    }

    // T-10-1343（監査 BLOCKING 1 の送り手側）: 上限ちょうど（2000 文字）の方針を切り詰めずに返す。
    [Fact]
    public async Task 上限ちょうどの方針を切り詰めずに返す()
    {
        var policy = string.Concat(Enumerable.Range(0, 2000).Select(i => (char)('あ' + (i % 80))));
        var json = JsonSerializer.Serialize(new { policySummary = policy, watchlistChanges = Array.Empty<object>() });
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, new RecordingGateway(json));
        await SeedDraftAsync(factory);

        var response = await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "a", periodKey = PeriodKey, onBehalfOf = "developer" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("policySummary").GetString().Should().Be(policy);
    }

    // T-10-1361（ADR-0042 決定 3・#1024）: 本番の組み立て（EF の台帳・構成 Reports:PolicyRevision:DailyLimit）で、上限を超えた
    // `/policy` は 429 で断られ、LLM は呼ばれず、報告書の版も進まない。
    [Fact]
    public async Task 一日の上限を超えた改訂は429でLLMを呼ばない()
    {
        var gateway = new RecordingGateway(ProposalJson);
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, gateway).WithWebHostBuilder(b =>
            b.UseSetting("Reports:PolicyRevision:DailyLimit", "1"));
        await SeedDraftAsync(factory);

        (await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "a", periodKey = PeriodKey, onBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var refused = await BotClient(factory).PostAsJsonAsync(
            "/reports/policy-revisions", new { instruction = "b", periodKey = PeriodKey, onBehalfOf = "developer" });

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("上限の 1 回");
        gateway.Bodies.Should().ContainSingle("2 回目は LLM を呼ばない");
        var report = await UserClient(factory).GetFromJsonAsync<JsonElement>($"/reports/{PeriodKey}");
        report.GetProperty("version").GetInt32().Should().Be(2);
    }

    // T-10-1387（ADR-0042 決定 1・#1025）: 本番の組み立てで、改訂の要求が運んだ監視銘柄と案の入れ替えを、確定した版から引ける。
    // 内訳の記録は 1 回だけ（2 回目は 409）。/policy の案でない版は 404。サービス主体は 403。
    [Fact]
    public async Task 確定した版の入れ替え案を引き内訳を一度だけ記録する()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, new RecordingGateway(ProposalJson));
        await SeedDraftAsync(factory);

        (await BotClient(factory).PostAsJsonAsync("/reports/policy-revisions", new
        {
            instruction = "a",
            periodKey = PeriodKey,
            onBehalfOf = "developer",
            currentWatchlist = new[] { new { symbol = "AAPL", market = "UnitedStates" } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // PR #1027 の監査 H1: 確定される前は照会できない（409）。
        (await BotClient(factory).GetAsync($"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=2"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "確定されていない版の案は適用の対象にならない");
        (await BotClient(factory).PostAsJsonAsync($"/reports/{PeriodKey}/confirm", new { ExpectedVersion = 2, OnBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var proposal = await BotClient(factory).GetFromJsonAsync<JsonElement>(
            $"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=2");
        proposal.GetProperty("reportVersion").GetInt32().Should().Be(2);
        proposal.GetProperty("changes").EnumerateArray().Single().GetProperty("symbol").GetString().Should().Be("NVDA");
        proposal.GetProperty("snapshot").EnumerateArray().Single().GetProperty("market").GetString().Should().Be("UnitedStates");
        proposal.GetProperty("applyRecorded").GetBoolean().Should().BeFalse();
        var attemptId = proposal.GetProperty("attemptId").GetGuid();

        (await BotClient(factory).GetAsync($"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=1"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "版 1 は /policy の案ではない");

        var record = new { outcome = "applied", items = new[] { new { action = "add", symbol = "NVDA", applied = true } }, message = "m", onBehalfOf = "developer" };
        (await BotClient(factory).PostAsJsonAsync($"/reports/policy-revisions/{attemptId}/watchlist-apply-result", record))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await BotClient(factory).PostAsJsonAsync($"/reports/policy-revisions/{attemptId}/watchlist-apply-result", record))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "内訳は 1 回だけ記録する");
        (await BotClient(factory).GetFromJsonAsync<JsonElement>($"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=2"))
            .GetProperty("applyRecorded").GetBoolean().Should().BeTrue();

        var service = factory.CreateClient();
        service.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-service");
        (await service.GetAsync($"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=2"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task SeedDraftAsync(WebApplicationFactory<Program> factory, string key)
    {
        (await UserClient(factory).PutAsJsonAsync($"/reports/{key}", new
        {
            Kind = "Daily",
            PeriodStart = "2026-09-10",
            BasedOn = (string?)null,
            AssumptionsVersion = 1,
            PolicySummary = "元の方針",
            ExpectedVersion = 0,
        })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> ReviseAsync(WebApplicationFactory<Program> factory, string key, object? currentWatchlist) =>
        BotClient(factory).PostAsJsonAsync("/reports/policy-revisions", new
        {
            instruction = "a",
            periodKey = key,
            onBehalfOf = "developer",
            currentWatchlist,
        });

    // T-10-1415（PR #1027 の監査 H1）: 版 2（案 A）と版 3（案 B）を作り、版 3 を確定した後で、古い版 2 の確定要求が来ても
    // ①応答は `transitioned=false` と確定後の版 4 を返し（版 2 の確定と読める値を返さない）②版 2 の案は照会できず（409）
    // ③版 2 の試行へ内訳も記録できない（409）。版 3 の案は照会でき、確定の時刻が台帳に残る（M1）。版 2 には残らない。
    [Fact]
    public async Task 別の版で確定済みなら古い版の案を照会も記録もさせない()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, new RecordingGateway(ProposalJson));
        await SeedDraftAsync(factory);
        var watchlist = new[] { new { symbol = "AAPL", market = "UnitedStates" } };

        (await ReviseAsync(factory, PeriodKey, watchlist)).StatusCode.Should().Be(HttpStatusCode.OK); // 版 2（案 A）
        (await ReviseAsync(factory, PeriodKey, watchlist)).StatusCode.Should().Be(HttpStatusCode.OK); // 版 3（案 B）

        var confirmed = await (await BotClient(factory).PostAsJsonAsync($"/reports/{PeriodKey}/confirm", new { ExpectedVersion = 3, OnBehalfOf = "developer" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        (confirmed.GetProperty("transitioned").GetBoolean(), confirmed.GetProperty("version").GetInt32()).Should().Be((true, 4));

        var stale = await BotClient(factory).PostAsJsonAsync($"/reports/{PeriodKey}/confirm", new { ExpectedVersion = 2, OnBehalfOf = "developer" });
        stale.StatusCode.Should().Be(HttpStatusCode.OK, "確定済みの再確定は冪等（IADR-0024 は変えない）");
        var staleBody = await stale.Content.ReadFromJsonAsync<JsonElement>();
        (staleBody.GetProperty("transitioned").GetBoolean(), staleBody.GetProperty("version").GetInt32())
            .Should().Be((false, 4), "版 2 で確定されたとは読めない値（2 + 1 ≠ 4）を返す");
        staleBody.GetProperty("state").GetString().Should().Be("Confirmed", "従来の報告書の項目も返す（追加だけ）");

        (await BotClient(factory).GetAsync($"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=2"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var v3 = await BotClient(factory).GetFromJsonAsync<JsonElement>($"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=3");
        v3.GetProperty("reportVersion").GetInt32().Should().Be(3);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportService.Infrastructure.Persistence.ReportDbContext>();
        var attempts = db.PolicyRevisionAttempts.OrderBy(a => a.ReportVersion).ToList();
        attempts.Select(a => (a.ReportVersion, a.ProposalConfirmedAt is not null)).Should().Equal((2, false), (3, true));

        var record = new { outcome = "applied", items = Array.Empty<object>(), message = "m", onBehalfOf = "developer" };
        (await BotClient(factory).PostAsJsonAsync($"/reports/policy-revisions/{attempts[0].Id}/watchlist-apply-result", record))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "確定されていない案の試行で適用を塞がない（L1）");
        (await BotClient(factory).PostAsJsonAsync($"/reports/policy-revisions/{Guid.NewGuid()}/watchlist-apply-result", record))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BotClient(factory).PostAsJsonAsync($"/reports/policy-revisions/{attempts[1].Id}/watchlist-apply-result", record))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // T-10-1416（PR #1027 の監査 M3・原則 A）: 改訂の要求の現在の監視銘柄が null（照会できなかった）／空の一覧／1 件のとき、
    // 確定した版の案の照会の `snapshot` はそれぞれ null／[]／1 件のまま返る（null と空を潰さない）。
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("one")]
    public async Task 監視銘柄の不明と空と有りを照会まで区別する(string kind)
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = Configure(baseFactory, new RecordingGateway(ProposalJson));
        await SeedDraftAsync(factory, PeriodKey);
        object? sent = kind switch
        {
            "null" => null,
            "empty" => Array.Empty<object>(),
            _ => new[] { new { symbol = "7203", market = "Japan" } },
        };

        (await ReviseAsync(factory, PeriodKey, sent)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await BotClient(factory).PostAsJsonAsync($"/reports/{PeriodKey}/confirm", new { ExpectedVersion = 2, OnBehalfOf = "developer" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var snapshot = (await BotClient(factory).GetFromJsonAsync<JsonElement>(
            $"/reports/policy-revisions/watchlist-proposal?periodKey={PeriodKey}&version=2")).GetProperty("snapshot");

        switch (kind)
        {
            case "null":
                snapshot.ValueKind.Should().Be(JsonValueKind.Null);
                break;
            case "empty":
                snapshot.ValueKind.Should().Be(JsonValueKind.Array);
                snapshot.GetArrayLength().Should().Be(0);
                break;
            default:
                snapshot.EnumerateArray().Single().GetProperty("symbol").GetString().Should().Be("7203");
                break;
        }
    }
}
