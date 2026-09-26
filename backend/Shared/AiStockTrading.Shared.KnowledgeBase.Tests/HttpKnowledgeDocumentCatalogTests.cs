using System.Net;
using System.Text.Json;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Adapters;
using AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiStockTrading.Shared.KnowledgeBase.Tests;

// FR-08, #1028, IADR-0436 決定 2・3: 基盤の文書台帳を保守の操作から使う HTTP アダプタ（T-10-1490〜T-10-1492）。
// 🔴 原則 A: 書き込みの「失敗（届いていない・拒否された）」と「結果不明（送った後に応答が来ない・5xx）」を分ける。
public class HttpKnowledgeDocumentCatalogTests
{
    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpKnowledgeDocumentCatalog Catalog(Handler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://documents") },
            NullLogger<HttpKnowledgeDocumentCatalog>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static Handler Throwing(Exception ex) => new((_, _) => throw ex);

    // ── T-10-1490: 一覧 ──

    // T-10-1490: GET /documents の応答から id・属性・本文の有無（MarkdownUri の有無）を読む。基盤の HasBody は読まない
    // （本文なしで作った文書でも既定 true のため）。
    [Fact]
    public async Task 一覧は本文の有無をMarkdownUriで読み_属性を運ぶ()
    {
        var withBody = Guid.NewGuid();
        var withoutBody = Guid.NewGuid();
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, $$"""
            [
              {"id":"{{withBody}}","title":"確定報告書 Daily daily-2026-07-10","markdownUri":"storage://docs/{{withBody}}.md","hasBody":true,
               "attributes":{"project":"ai-stock-trading","periodKey":"daily-2026-07-10","kind":"Daily"},"updatedAt":"2026-07-10T10:00:00+00:00"},
              {"id":"{{withoutBody}}","title":"t","markdownUri":null,"hasBody":true,"attributes":{"periodKey":"daily-2026-07-11"},"updatedAt":"2026-07-11T10:00:00+00:00"}
            ]
            """));

        var result = await Catalog(handler).ListAsync();

        result.Outcome.Should().Be(KnowledgeCatalogOutcome.Succeeded);
        result.Entries.Should().HaveCount(2);
        var first = result.Entries.Single(e => e.DocumentId == withBody);
        first.HasStoredBody.Should().BeTrue();
        first.Attributes["periodKey"].Should().Be("daily-2026-07-10");
        first.Attributes["kind"].Should().Be("Daily");
        result.Entries.Single(e => e.DocumentId == withoutBody).HasStoredBody.Should().BeFalse("hasBody=true でも MarkdownUri が無ければ本文は無い");
        handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/documents");
    }

    // T-10-1490: 非 2xx・解釈できない応答は失敗、タイムアウトは不明（理由で分ける）。どれも空の一覧を「成功」と返さない。
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "not-json", KnowledgeCatalogOutcome.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, "{}", KnowledgeCatalogOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, "not-json", KnowledgeCatalogOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, "null", KnowledgeCatalogOutcome.Failed)]
    public async Task 一覧を読めなければ成功にしない(HttpStatusCode status, string body, KnowledgeCatalogOutcome expected)
    {
        var result = await Catalog(new Handler((_, _) => Json(status, body))).ListAsync();

        result.Outcome.Should().Be(expected);
        result.Entries.Should().BeEmpty();
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task 一覧のタイムアウトは不明と書く()
    {
        var result = await Catalog(Throwing(new TaskCanceledException("timeout"))).ListAsync();

        result.Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);
        result.Reason.Should().Contain("タイムアウト");
    }

    [Fact]
    public async Task 一覧の接続失敗は失敗と書く()
    {
        var result = await Catalog(Throwing(new HttpRequestException(HttpRequestError.ConnectionError, "refused"))).ListAsync();

        result.Outcome.Should().Be(KnowledgeCatalogOutcome.Failed);
    }

    // 🔴 呼び出し元のキャンセルは握りつぶさない（タイムアウトと取り違えない）。
    [Fact]
    public async Task 呼び出し元のキャンセルは伝播する()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var catalog = Catalog(new Handler((_, _) => throw new TaskCanceledException()));

        var act = () => catalog.ListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // T-10-1490: 未構成なら NotConfigured（空の一覧のふりをしない）。構成済みなら HTTP の台帳。
    [Fact]
    public async Task 未構成の台帳はNotConfiguredを返し構成済みならHTTPの台帳を選ぶ()
    {
        static IKnowledgeDocumentCatalog Resolve(string? baseUrl)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["KnowledgeBase:Documents:BaseUrl"] = baseUrl })
                .Build();
            var services = new ServiceCollection().AddLogging();
            services.AddAiStockTradingKnowledgeDocumentCatalog(config);
            return services.BuildServiceProvider().GetRequiredService<IKnowledgeDocumentCatalog>();
        }

        var notConfigured = Resolve(null);
        (await notConfigured.ListAsync()).Outcome.Should().Be(KnowledgeCatalogOutcome.NotConfigured);
        (await notConfigured.CreateAsync(new KnowledgeDocument("t"))).Outcome.Should().Be(KnowledgeCatalogOutcome.NotConfigured);
        (await notConfigured.PutBodyAsync(Guid.NewGuid(), "b")).Outcome.Should().Be(KnowledgeCatalogOutcome.NotConfigured);
        Resolve("not a uri").Should().BeOfType<NotConfiguredKnowledgeDocumentCatalog>();
        Resolve("http://documents").Should().BeOfType<HttpKnowledgeDocumentCatalog>();
        KnowledgeDocumentCatalogExtensions.CatalogTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    // ── T-10-1491: 作成 ──

    // T-10-1491: 本文つきで POST /documents へ送り、属性は保存ポートと同じ規則で補完する（project・機密区分）。201 の id を返す。
    [Fact]
    public async Task 作成は本文つきで送り文書IDを返す()
    {
        var id = Guid.NewGuid();
        var handler = new Handler((_, _) => Json(HttpStatusCode.Created, $$"""{"id":"{{id}}"}"""));

        var result = await Catalog(handler).CreateAsync(new KnowledgeDocument(
            "確定報告書 Daily daily-2026-07-10", Content: "# 日報", Tags: ["report", "daily"], ContentType: "text/markdown",
            Attributes: new Dictionary<string, string> { ["periodKey"] = "daily-2026-07-10", ["kind"] = "Daily" }));

        result.Should().Be(KnowledgeCatalogWriteResult.Ok(id));
        var (method, path, body) = handler.Requests.Should().ContainSingle().Subject;
        method.Should().Be(HttpMethod.Post);
        path.Should().Be("/documents");
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("body").GetString().Should().Be("# 日報");
        var attributes = json.RootElement.GetProperty("attributes");
        attributes.GetProperty("project").GetString().Should().Be("ai-stock-trading");
        attributes.GetProperty("confidentiality").GetString().Should().Be("internal");
        attributes.GetProperty("periodKey").GetString().Should().Be("daily-2026-07-10");
    }

    // T-10-1491: 4xx は失敗（理由に状態コードと応答の抜粋。制御文字は潰し長さを切る）。5xx は不明（保存の後に落ち得る）。
    [Fact]
    public async Task 作成の4xxは失敗で理由に応答の抜粋を載せる()
    {
        var problem = "{\"errors\":{\"tags\":[\"辞書に無いタグです: report\"]}}\n\u001b[31m" + new string('x', 500);
        var result = await Catalog(new Handler((_, _) => Json(HttpStatusCode.BadRequest, problem)))
            .CreateAsync(new KnowledgeDocument("t", Content: "b"));

        result.Outcome.Should().Be(KnowledgeCatalogOutcome.Failed);
        result.DocumentId.Should().BeNull();
        result.Reason.Should().Contain("HTTP 400").And.Contain("辞書に無いタグです");
        result.Reason.Should().NotContain("\u001b").And.NotContain("\n");
        result.Reason!.Length.Should().BeLessThan(HttpKnowledgeDocumentCatalog.MaxReasonExcerptLength + 100);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task 作成の5xxは不明と書く(HttpStatusCode status)
    {
        var result = await Catalog(new Handler((_, _) => new HttpResponseMessage(status)))
            .CreateAsync(new KnowledgeDocument("t", Content: "b"));

        result.Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);
        result.Reason.Should().Contain("保存された可能性");
    }

    // T-10-1491: タイムアウト・送った後の切断・2xx なのに ID が無い＝不明。接続できなかった（届いていない）＝失敗。
    [Fact]
    public async Task 作成のタイムアウトと送信後の切断は不明_接続できなければ失敗()
    {
        var doc = new KnowledgeDocument("t", Content: "b");

        (await Catalog(Throwing(new TaskCanceledException("timeout"))).CreateAsync(doc))
            .Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);
        (await Catalog(Throwing(new HttpRequestException(HttpRequestError.ResponseEnded, "ended"))).CreateAsync(doc))
            .Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);
        (await Catalog(new Handler((_, _) => Json(HttpStatusCode.Created, "{}"))).CreateAsync(doc))
            .Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);

        foreach (var notDelivered in new[]
                 {
                     HttpRequestError.ConnectionError, HttpRequestError.NameResolutionError,
                     HttpRequestError.SecureConnectionError, HttpRequestError.ProxyTunnelError,
                 })
        {
            (await Catalog(Throwing(new HttpRequestException(notDelivered, "x"))).CreateAsync(doc))
                .Outcome.Should().Be(KnowledgeCatalogOutcome.Failed, $"{notDelivered} は要求が届いていない");
        }
    }

    // ── T-10-1492: 本文の投入 ──

    // T-10-1492: PUT /documents/{id}/body に本文だけを送る。成功は同じ文書 ID（文書は増えない）。
    [Fact]
    public async Task 本文の投入は既存文書へ本文だけを送る()
    {
        var id = Guid.NewGuid();
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, $$"""{"id":"{{id}}"}"""));

        var result = await Catalog(handler).PutBodyAsync(id, "# 日報");

        result.Should().Be(KnowledgeCatalogWriteResult.Ok(id));
        var (method, path, body) = handler.Requests.Should().ContainSingle().Subject;
        method.Should().Be(HttpMethod.Put);
        path.Should().Be($"/documents/{id}/body");
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("body").GetString().Should().Be("# 日報");
    }

    // T-10-1492: 404 は失敗（所有者でない可能性を理由に書く）・413 は失敗・5xx とタイムアウトは不明。
    [Fact]
    public async Task 本文の投入の拒否は失敗_5xxとタイムアウトは不明()
    {
        var id = Guid.NewGuid();

        var notFound = await Catalog(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))).PutBodyAsync(id, "b");
        notFound.Outcome.Should().Be(KnowledgeCatalogOutcome.Failed);
        notFound.Reason.Should().Contain("所有者").And.Contain("HTTP 404");

        (await Catalog(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge))).PutBodyAsync(id, "b"))
            .Outcome.Should().Be(KnowledgeCatalogOutcome.Failed);
        (await Catalog(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).PutBodyAsync(id, "b"))
            .Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);
        (await Catalog(Throwing(new TaskCanceledException("timeout"))).PutBodyAsync(id, "b"))
            .Outcome.Should().Be(KnowledgeCatalogOutcome.Unknown);
    }
}
