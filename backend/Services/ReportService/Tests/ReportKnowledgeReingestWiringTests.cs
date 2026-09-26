using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiStockTrading.Shared.KnowledgeBase;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Domain;
using ReportService.Features.Reports.ReingestKnowledgeBase;
using Xunit;
using static ReportService.Tests.ReportKnowledgeReingestTestKit;

namespace ReportService.Tests;

// FR-08, FR-11, #1028, IADR-0436: 入れ直しの口を**本番の Program.cs の組み立て**で通す（T-10-1502〜T-10-1504）。
// T-10-1503 は KB の台帳も本物（HttpKnowledgeDocumentCatalog）で、名前付きクライアント "kb-documents-maintenance" の
// 一次ハンドラだけを基盤の文書 API の模造（作成は毎回新しい文書・一覧は全件）へ差し替える。実 KB へは接続しない。
public class ReportKnowledgeReingestWiringTests
{
    // 基盤 DocumentService の 3 口（GET /documents・POST /documents・PUT /documents/{id}/body）の模造。
    private sealed class DocumentServiceStub : HttpMessageHandler
    {
        private sealed record StoredDoc(Guid Id, Dictionary<string, string> Attributes, string? Body, DateTimeOffset UpdatedAt);

        private readonly ConcurrentDictionary<Guid, StoredDoc> _docs = new();

        public int Posts;
        public int Puts;
        public int Gets;
        public string? LastAuthorization;

        public IReadOnlyCollection<(Dictionary<string, string> Attributes, string? Body)> Documents =>
            [.. _docs.Values.Select(d => (d.Attributes, d.Body))];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/documents")
            {
                Interlocked.Increment(ref Gets);
                return JsonResponse(HttpStatusCode.OK, _docs.Values.Select(d => new
                {
                    id = d.Id,
                    title = "t",
                    markdownUri = d.Body is null ? null : $"storage://docs/{d.Id}.md",
                    hasBody = true,
                    attributes = d.Attributes,
                    updatedAt = d.UpdatedAt,
                }));
            }

            if (request.Method == HttpMethod.Post && path == "/documents")
            {
                Interlocked.Increment(ref Posts);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var attributes = json.RootElement.GetProperty("attributes").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
                var body = json.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                var doc = new StoredDoc(Guid.NewGuid(), attributes, body, DateTimeOffset.UtcNow);
                _docs[doc.Id] = doc;
                return JsonResponse(HttpStatusCode.Created, new { id = doc.Id });
            }

            if (request.Method == HttpMethod.Put && path.EndsWith("/body", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Puts);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode status, object value) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private static WebApplicationFactory<Program> WithDocumentService(
        ReportWorkerWebApplicationFactory baseFactory, DocumentServiceStub stub) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("KnowledgeBase:Documents:BaseUrl", "http://document-service");
            b.ConfigureServices(services =>
                services.AddHttpClient("kb-documents-maintenance").ConfigurePrimaryHttpMessageHandler(() => stub));
        });

    // T-10-1502（受け入れ基準 10）: 所有者専用。未認証は 401、サービス主体（trading-service）は 403。どちらも KB に触れない。
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("trading-service", HttpStatusCode.Forbidden)]
    [InlineData("trading-viewer", HttpStatusCode.Forbidden)]
    public async Task 所有者以外は入れ直せない(string? roles, HttpStatusCode expected)
    {
        var kb = new FakeKnowledgeCatalog();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, new DateOnly(2026, 7, 10), "# A");

        var client = factory.CreateClient();
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);

        var (response, _, audits) = await RunAsync(factory, new { all = true }, client);

        response.StatusCode.Should().Be(expected);
        kb.CreateCalls.Should().Be(0);
        audits.Should().BeEmpty();
    }

    // T-10-1503（受け入れ基準 11・1・2）: 本物の台帳のアダプタで、確定済みの報告書が本文つきで基盤へ作られ、2 回目は作らない。
    // 監査の操作者はトークンの主体（名前クレームの無い機密クライアントなら client:<azp>）。
    [Fact]
    public async Task 本番の組み立てで確定済みを本文つきで入れ_二回目は作らない()
    {
        var stub = new DocumentServiceStub();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithDocumentService(baseFactory, stub);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, new DateOnly(2026, 7, 10), "# 日報 07-10");
        Seed(factory.Services, "monthly-2026-07", ReportKind.Monthly, new DateOnly(2026, 7, 1), "# 月報 07");

        var (first, firstResult, firstAudits) = await RunAsync(factory, new { all = true });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        firstResult!.Created.Should().Be(2);
        stub.Posts.Should().Be(2);
        stub.Documents.Should().HaveCount(2);
        var daily = stub.Documents.Single(d => d.Attributes["periodKey"] == "daily-2026-07-10");
        daily.Body.Should().Be("# 日報 07-10");
        daily.Attributes["kind"].Should().Be("Daily");
        daily.Attributes["project"].Should().Be("ai-stock-trading");
        daily.Attributes["confidentiality"].Should().Be("internal");
        firstAudits.Should().ContainSingle().Which.Actor.Should().Be("owner");

        var bot = factory.CreateClient();
        bot.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        bot.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        bot.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");
        var (second, secondResult, secondAudits) = await RunAsync(factory, new { all = true, onBehalfOf = "someone" }, bot);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.Posts.Should().Be(2, "2 回目は作らない");
        stub.Documents.Should().HaveCount(2);
        secondResult!.AlreadyPresent.Should().Be(2);
        secondAudits.Should().ContainSingle().Which.Actor.Should().Be("client:ai-stock-trading-owner",
            "代理の窓口を持たない操作なので本文の名前は信じず、トークンの主体を残す");
    }

    // T-10-1503: 本物の台帳で本文なしの写しへの本文の投入が 404（所有者でない）なら失敗として作らない。
    [Fact]
    public async Task 本番の組み立てで本文の投入を拒否されたら失敗として作らない()
    {
        var stub = new DocumentServiceStub();
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithDocumentService(baseFactory, stub);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, new DateOnly(2026, 7, 10), string.Empty);
        await RunAsync(factory, new { all = true });
        stub.Documents.Should().BeEmpty("本文の空の報告書は作らない");

        // 本文なしの写しを基盤側へ置く（旧経路で入った #565 の写し）。
        using (var scope = factory.Services.CreateScope())
        {
            var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("kb-documents-maintenance");
            http.BaseAddress = new Uri("http://document-service");
            (await http.PostAsJsonAsync("/documents", new
            {
                title = "確定報告書 Daily daily-2026-07-13",
                attributes = new Dictionary<string, string> { ["project"] = "ai-stock-trading", ["periodKey"] = "daily-2026-07-13", ["kind"] = "Daily" },
                tags = new[] { "report" },
            })).StatusCode.Should().Be(HttpStatusCode.Created);
        }
        Seed(factory.Services, "daily-2026-07-13", ReportKind.Daily, new DateOnly(2026, 7, 13), "# 日報 07-13");
        var postsBefore = stub.Posts;

        var (_, result, audits) = await RunAsync(factory, new { all = true });

        var item = result!.Items.Single(i => i.PeriodKey == "daily-2026-07-13");
        item.Outcome.Should().Be(ReportKnowledgeReingestOutcome.Failed);
        item.Reason.Should().Contain("所有者").And.Contain("HTTP 404");
        stub.Puts.Should().Be(1);
        stub.Posts.Should().Be(postsBefore, "拒否されても別の文書を作らない");
        audits.Should().ContainSingle().Which.Failed.Should().Be(1);
    }

    // T-10-1504（受け入れ基準 12）: 実行中の 2 本目は 409 で何もしない（同時の 2 本が同じ「無い」を見て両方作ると重複する）。
    [Fact]
    public async Task 実行中の二本目は409で何もしない()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kb = new FakeKnowledgeCatalog
        {
            BeforeList = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            },
        };
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = WithCatalog(baseFactory, kb);
        Seed(factory.Services, "daily-2026-07-10", ReportKind.Daily, new DateOnly(2026, 7, 10), "# A");

        var first = OwnerClient(factory).PostAsJsonAsync(Route, new { all = true });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        kb.BeforeList = null;
        var second = await OwnerClient(factory).PostAsJsonAsync(Route, new { all = true });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("実行中");

        release.SetResult();
        (await first).StatusCode.Should().Be(HttpStatusCode.OK);
        kb.Docs.Should().ContainSingle();

        // 終われば次は通る（ゲートを返している）。
        (await OwnerClient(factory).PostAsJsonAsync(Route, new { all = true })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 本番の組み立てが入れ直しの型を解決できる（ゲートは singleton・サービスは scoped）。
    [Fact]
    public async Task 入れ直しの型は本番の組み立てで解決できる()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ReportKnowledgeReingestService>().Should().NotBeNull();
        factory.Services.GetRequiredService<ReportKnowledgeReingestGate>()
            .Should().BeSameAs(factory.Services.GetRequiredService<ReportKnowledgeReingestGate>());
        KnowledgeBodyLimits.MaxBytes.Should().Be(1024 * 1024);
    }
}
