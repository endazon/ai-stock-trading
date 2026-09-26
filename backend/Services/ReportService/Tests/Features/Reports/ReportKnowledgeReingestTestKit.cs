using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AiStockTrading.TestSupport.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Features.Reports.ReingestKnowledgeBase;
using Wolverine.Tracking;

namespace ReportService.Tests;

// FR-08, #1028, IADR-0436: 確定報告書の KB への入れ直しの試験の道具（本番の Program.cs の組み立てを使う）。
internal static class ReportKnowledgeReingestTestKit
{
    public const string OwnerRole = "trading-owner";
    public const string Route = "/reports/knowledge-base/reingest";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // 台帳（KB）を差し替えた本番の組み立て。catalog が null なら本番の選択（構成に従う）のまま。
    public static WebApplicationFactory<Program> WithCatalog(
        ReportWorkerWebApplicationFactory baseFactory, IKnowledgeDocumentCatalog? catalog) =>
        baseFactory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            if (catalog is null)
                return;
            services.RemoveAll<IKnowledgeDocumentCatalog>();
            services.AddSingleton(catalog);
        }));

    public static HttpClient OwnerClient(WebApplicationFactory<Program> factory, string name = "owner")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, name);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, "ai-stock-trading-dev");
        return client;
    }

    // 確定報告書（または下書き）をストアへ直接置く（手動の PUT は本文を受け取らないため）。
    public static void Seed(
        IServiceProvider services, string periodKey, ReportKind kind, DateOnly periodStart, string body, bool confirm = true)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        var version = store.UpsertDraft(new TradingReport
        {
            PeriodKey = periodKey,
            Kind = kind,
            PeriodStart = periodStart,
            AssumptionsVersion = 1,
            PolicySummary = "方針",
            Body = body,
        }, 0);
        if (confirm)
            store.Confirm(periodKey, version, new DateTimeOffset(periodStart.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero));
    }

    public static async Task<(HttpResponseMessage Response, ReportKnowledgeReingestResult? Result, ReportKnowledgeReingested[] Audits)> RunAsync(
        WebApplicationFactory<Program> factory, object request, HttpClient? client = null)
    {
        client ??= OwnerClient(factory);
        HttpResponseMessage response = null!;
        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            response = await client.PostAsJsonAsync(Route, request);
        });

        var text = await response.Content.ReadAsStringAsync();
        ReportKnowledgeReingestResult? result = null;
        if (text.Contains("\"runId\"", StringComparison.Ordinal))
            result = JsonSerializer.Deserialize<ReportKnowledgeReingestResult>(text, Json);

        return (response, result, [.. session.Sent.MessagesOf<ReportKnowledgeReingested>()]);
    }
}

// FR-08, #1028: 基盤の文書台帳を模した KB（MSP DocumentService の意味: 作成は毎回新しい文書・本文の投入は所有者だけ・
// 一覧は全件）。書き込みの失敗・不明を報告書ごとに差し込める。
internal sealed class FakeKnowledgeCatalog : IKnowledgeDocumentCatalog
{
    public sealed class Doc
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Title { get; init; } = "t";
        public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.Ordinal);
        public string? Body { get; set; }
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool OwnedByAst { get; init; } = true;
    }

    public List<Doc> Docs { get; } = [];
    public int CreateCalls { get; private set; }
    public int PutCalls { get; private set; }

    public KnowledgeCatalogListResult? ListOverride { get; set; }

    // 期間キーごとの作成の振る舞い（"saved-unknown"＝保存したのに結果は不明／"failed"＝400 で拒否）。
    public Dictionary<string, string> CreateBehavior { get; } = new(StringComparer.Ordinal);

    // 文書ごとの本文の投入の結果の差し込み（404 以外の失敗・不明）。
    public Dictionary<Guid, KnowledgeCatalogWriteResult> PutOverride { get; } = [];

    public Func<Task>? BeforeList { get; set; }

    // project が null なら project 属性を持たない（#665 より前の保存の形）。表題の既定は確定時の写像の表題。
    public Doc AddExisting(string periodKey, string kind, string? body, bool ownedByAst = true, string? project = "ai-stock-trading",
        DateTimeOffset? updatedAt = null, string? title = null)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["periodKey"] = periodKey, ["kind"] = kind };
        if (project is not null)
            attributes["project"] = project;
        var doc = new Doc
        {
            Title = title ?? $"確定報告書 {kind} {periodKey}",
            Attributes = attributes,
            Body = body,
            OwnedByAst = ownedByAst,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
        Docs.Add(doc);
        return doc;
    }

    public async Task<KnowledgeCatalogListResult> ListAsync(CancellationToken cancellationToken = default)
    {
        if (BeforeList is not null)
            await BeforeList();
        return ListOverride ?? KnowledgeCatalogListResult.Ok([.. Docs.Select(d => new KnowledgeCatalogEntry(
            d.Id, d.Title, new Dictionary<string, string>(d.Attributes, StringComparer.Ordinal), d.Body is not null, d.UpdatedAt))]);
    }

    public Task<KnowledgeCatalogWriteResult> CreateAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
    {
        CreateCalls++;
        var periodKey = document.Attributes!["periodKey"];
        CreateBehavior.TryGetValue(periodKey, out var behavior);
        if (behavior == "failed")
            return Task.FromResult(KnowledgeCatalogWriteResult.Failed("KB への文書の作成を拒否されました（HTTP 400: 辞書に無いタグです: report）。", 400));

        var doc = new Doc
        {
            Title = document.Title,
            Attributes = new(document.Attributes!, StringComparer.Ordinal) { ["project"] = "ai-stock-trading" },
            Body = document.Content,
        };
        Docs.Add(doc);
        return Task.FromResult(behavior == "saved-unknown"
            ? KnowledgeCatalogWriteResult.Unknown("KB への文書の作成がタイムアウトしました（結果が分かりません。保存された可能性があります）。")
            : KnowledgeCatalogWriteResult.Ok(doc.Id));
    }

    public Task<KnowledgeCatalogWriteResult> PutBodyAsync(Guid documentId, string body, CancellationToken cancellationToken = default)
    {
        PutCalls++;
        if (PutOverride.TryGetValue(documentId, out var injected))
            return Task.FromResult(injected);
        var doc = Docs.SingleOrDefault(d => d.Id == documentId);
        if (doc is null || !doc.OwnedByAst)
            return Task.FromResult(KnowledgeCatalogWriteResult.Failed(
                "KB の文書への本文の投入を拒否されました（文書が無いか、AST の KB 用クライアントが所有者ではありません）（HTTP 404）。", 404));
        doc.Body = body;
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(KnowledgeCatalogWriteResult.Ok(documentId));
    }
}
