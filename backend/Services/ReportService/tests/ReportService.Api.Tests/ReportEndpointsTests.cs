using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.KnowledgeBase;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wolverine.Tracking;
using Xunit;

namespace AiStockTrading.Report.Api.Tests;

// FR-06/07, UC-03〜05, ADR-0007: 報告書エンドポイントの OwnerOnly 認可・ドラフト upsert・版番号付き冪等確定・
// 確定済み日報方針照会・確定イベント発行を WebApplicationFactory で検証する。テストごとに独立 Factory（独立 InMemory DB）。
public class ReportEndpointsTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";

    private static HttpClient OwnerClient(ReportWorkerWebApplicationFactory factory) =>
        ClientWithRoles(factory, OwnerRole);

    private static HttpClient ClientWithRoles(ReportWorkerWebApplicationFactory factory, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static object DraftBody(string policy = "翌営業日は押し目買い", int expectedVersion = 0) => new
    {
        Kind = "Daily",
        PeriodStart = "2026-07-10",
        BasedOn = (string?)null,
        AssumptionsVersion = 1,
        PolicySummary = policy,
        ExpectedVersion = expectedVersion,
    };

    [Fact]
    public async Task 未認証は_401_ロール無しは_403()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await factory.CreateClient().GetAsync("/reports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");
        (await noRole.GetAsync("/reports")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ドラフト作成_確定で_ReportConfirmed_発行_daily_policy_照会()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        var put = await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // ADR-0013, IADR-0129, #354: MassTransit の ITestHarness に代えて Wolverine.Tracking で発行を捕捉する
        // （確定の処理中に外へ出たメッセージが収束するまで待つ）。
        HttpResponseMessage confirm = null!;
        var session = await factory.Services.ExecuteAndWaitAsync(async () =>
        {
            confirm = await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 });
        });
        confirm.StatusCode.Should().Be(HttpStatusCode.OK);

        // ReportConfirmed が発行される（IADR-0129 決定 2: 宛先はメッセージ型ごとの共有 fanout exchange）。
        session.Sent.MessagesOf<ReportConfirmed>().Should().NotBeEmpty();
        session.Sent.Envelopes().Should().Contain(e =>
            e.Message is ReportConfirmed
            && e.Destination!.ToString()
                == "rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.ReportConfirmed");

        // 確定済み日報方針が照会できる。
        var policy = await client.GetFromJsonAsync<DailyPolicyDto>("/reports/daily-policy");
        policy!.Summary.Should().Be("翌営業日は押し目買い");
        policy.AssumptionsVersion.Should().Be(1);
    }

    [Fact]
    public async Task サービスロールは確定済み_daily_policy_を照会できる_一覧_確定は不可()
    {
        // IADR-0051: daily-policy(GET) は OwnerOrService（取引判断#11 の s2s 照会）。一覧/確定は OwnerOnly のまま。
        await using var factory = new ReportWorkerWebApplicationFactory();

        // 利用者が確定させる（前提）。
        var owner = OwnerClient(factory);
        await owner.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());
        (await owner.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var service = ClientWithRoles(factory, ServiceRole);

        // 読み取り（daily-policy）は 200。
        var policy = await service.GetFromJsonAsync<DailyPolicyDto>("/reports/daily-policy");
        policy!.Summary.Should().Be("翌営業日は押し目買い");

        // 一覧（OwnerOnly）は 403。確定（OwnerOnly）も 403。
        (await service.GetAsync("/reports")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await service.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 版番号が不一致の確定は_409()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());

        var confirm = await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 99 });

        confirm.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 存在しない報告書の確定は_404()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var confirm = await OwnerClient(factory).PostAsJsonAsync("/reports/daily-nope/confirm", new { ExpectedVersion = 1 });

        confirm.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 確定が無ければ_daily_policy_は_404()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await OwnerClient(factory).GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 損益集計エンドポイントも_OwnerOnly_未認証は_401()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var res = await factory.CreateClient().PostAsJsonAsync("/reports/pnl-summary", new { Fills = Array.Empty<object>(), CurrentPrices = (object?)null });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 損益集計エンドポイントは実現損益を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var body = new
        {
            Fills = new[]
            {
                new { Symbol = "AAPL", Market = "UnitedStates", Side = "Buy", PositionEffect = "Open", Quantity = 10, Price = 1000m, ExecutedAt = "2026-07-10T00:00:00Z" },
                new { Symbol = "AAPL", Market = "UnitedStates", Side = "Sell", PositionEffect = "Close", Quantity = 10, Price = 1200m, ExecutedAt = "2026-07-10T00:01:00Z" },
            },
            CurrentPrices = (object?)null,
        };

        var res = await OwnerClient(factory).PostAsJsonAsync("/reports/pnl-summary", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await res.Content.ReadFromJsonAsync<PnlSummaryDto>();
        summary!.RealizedPnlGross.Should().Be(2_000m);
        summary.RealizingTradeCount.Should().Be(1);
    }

    [Fact]
    public async Task 日報ドラフト生成は_OwnerOnly_未認証は_401()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var res = await factory.CreateClient().PostAsJsonAsync("/reports/daily-2026-07-10/draft",
            new { Date = "2026-07-10", AssumptionsVersion = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 日報ドラフト生成は利用者ロール無しは_403()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");

        var res = await client.PostAsJsonAsync("/reports/daily-2026-07-10/draft",
            new { Date = "2026-07-10", AssumptionsVersion = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 日報ドラフトのPeriodKeyと対象日が不整合なら_400()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        var res = await OwnerClient(factory).PostAsJsonAsync("/reports/daily-2026-07-10/draft",
            new { Date = "2026-07-11", AssumptionsVersion = 1 });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 日報ドラフト生成は数値集計を含むMarkdownを返す()
    {
        // FR-06/16, IADR-0032: 数値はコード集計・散文は LLM ドラフト（安全既定プレースホルダ）。
        await using var factory = new ReportWorkerWebApplicationFactory();

        var body = new
        {
            Kind = "Daily",
            Date = "2026-07-10",
            Markets = new[] { "US" },
            AssumptionsVersion = 1,
            BasedOn = (string?)null,
            PolicySummary = "翌営業日は押し目買い",
            Fills = new[]
            {
                new { Symbol = "AAPL", Market = "UnitedStates", Side = "Buy", PositionEffect = "Open", Quantity = 10, Price = 1000m, ExecutedAt = "2026-07-10T00:00:00Z" },
                new { Symbol = "AAPL", Market = "UnitedStates", Side = "Sell", PositionEffect = "Close", Quantity = 10, Price = 1200m, ExecutedAt = "2026-07-10T00:01:00Z" },
            },
            CurrentPrices = (object?)null,
        };

        var res = await OwnerClient(factory).PostAsJsonAsync("/reports/daily-2026-07-10/draft", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var draft = await res.Content.ReadFromJsonAsync<DraftDto>();
        draft!.Markdown.Should().Contain("# 日報 2026-07-10");
        draft.Markdown.Should().Contain("report_type: daily");
        draft.Markdown.Should().Contain("翌営業日は押し目買い");
        draft.Pnl.RealizedPnlGross.Should().Be(2_000m);
    }

    [Fact]
    public async Task 週報ドラフト生成は週間サマリを含むMarkdownを返す()
    {
        // FR-06/16: Kind=Weekly・ISO 週 PeriodKey で週報を生成する。
        await using var factory = new ReportWorkerWebApplicationFactory();

        var body = new
        {
            Kind = "Weekly",
            Date = "2026-07-06", // ISO 週 2026-W28
            Markets = new[] { "US" },
            AssumptionsVersion = 1,
            BasedOn = (string?)null,
            PolicySummary = "翌週は半導体重点",
            Fills = Array.Empty<object>(),
            CurrentPrices = (object?)null,
        };

        var res = await OwnerClient(factory).PostAsJsonAsync("/reports/weekly-2026-W28/draft", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var draft = await res.Content.ReadFromJsonAsync<DraftDto>();
        draft!.Markdown.Should().Contain("# 週報 2026-W28");
        draft.Markdown.Should().Contain("report_type: weekly");
        draft.Markdown.Should().Contain("## 1. 週間サマリ");
        draft.Markdown.Should().Contain("翌週は半導体重点");
    }

    // FR-07, UC-03〜05, IADR-0042/0071 決定5: 対話的確定のレビュー結線（提示・差し戻し・照会・確定連携）。
    [Fact]
    public async Task 提示_差し戻し_のレビュー遷移と照会()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());

        // 初期はレビュー局面 Drafting。
        (await client.GetFromJsonAsync<ReviewDto>("/reports/daily-2026-07-10/review"))!.State.Should().Be("Drafting");

        // 提示 → PendingApproval（版は不変）。
        var present = await client.PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 1 });
        present.StatusCode.Should().Be(HttpStatusCode.OK);
        (await present.Content.ReadFromJsonAsync<ReviewDto>())!.State.Should().Be("PendingApproval");

        // 差し戻し → ChangesRequested。
        var rc = await client.PostAsJsonAsync("/reports/daily-2026-07-10/request-changes", new { ExpectedVersion = 1 });
        rc.StatusCode.Should().Be(HttpStatusCode.OK);
        (await rc.Content.ReadFromJsonAsync<ReviewDto>())!.State.Should().Be("ChangesRequested");
    }

    [Fact]
    public async Task レビュー操作は_未認証は_401_ロール無しは_403()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await factory.CreateClient().PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");
        (await noRole.PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 版番号不一致の提示は_409()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());

        (await client.PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 99 }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 確定済みへの提示は_409()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);
        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());
        await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 });

        (await client.PostAsJsonAsync("/reports/daily-2026-07-10/present", new { ExpectedVersion = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task 存在しない報告書のレビュー照会は_404()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        (await OwnerClient(factory).GetAsync("/reports/daily-nope/review")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-06, UC-03, IADR-0071 決定4: 初回月報ブートストラップは初期監視銘柄（構成）を含む月報ドラフトを返す。
    [Fact]
    public async Task 初回月報ブートストラップは初期監視銘柄を含む月報ドラフトを返す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reports:Bootstrap:Watchlist:0"] = "AAPL",
                ["Reports:Bootstrap:Watchlist:1"] = "7203",
            })));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);

        var res = await client.GetAsync("/reports/monthly-bootstrap");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await res.Content.ReadFromJsonAsync<BootstrapDto>();
        report!.Kind.Should().Be("Monthly");
        report.PolicySummary.Should().Contain("AAPL");
        report.PolicySummary.Should().Contain("7203");
    }

    [Fact]
    public async Task 初回月報ブートストラップは_未認証は_401_ロール無しは_403()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        (await factory.CreateClient().GetAsync("/reports/monthly-bootstrap")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noRole = factory.CreateClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "other");
        (await noRole.GetAsync("/reports/monthly-bootstrap")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 確定済み月報があればブートストラップは_404()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        var client = OwnerClient(factory);

        // 月報を作成・確定（ブートストラップ済み）。
        await client.PutAsJsonAsync("/reports/monthly-2026-06", new
        {
            Kind = "Monthly",
            PeriodStart = "2026-06-01",
            BasedOn = (string?)null,
            AssumptionsVersion = 1,
            PolicySummary = "6月方針",
            ExpectedVersion = 0,
        });
        (await client.PostAsJsonAsync("/reports/monthly-2026-06/confirm", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.GetAsync("/reports/monthly-bootstrap")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // FR-08, IADR-0069/0071 決定3: 確定遷移で確定報告書が KB へ保存される（既定 no-op を記録用に差し替えて検証）。
    [Fact]
    public async Task 確定すると確定報告書が_KB_へ保存される()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var recorder = new RecordingKnowledgeBaseWriter();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IKnowledgeBaseWriter>();
                s.AddSingleton<IKnowledgeBaseWriter>(recorder);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);

        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());
        (await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        recorder.Saved.Should().ContainSingle();
        recorder.Saved[0].Title.Should().Be("確定報告書 Daily daily-2026-07-10");
        recorder.Saved[0].Confidentiality.Should().Be(KnowledgeConfidentiality.Internal);
    }

    // 冪等な再確定（遷移なし）では KB 保存を重ねない。
    [Fact]
    public async Task 冪等な再確定では_KB_保存を重ねない()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var recorder = new RecordingKnowledgeBaseWriter();
        await using var factory = baseFactory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IKnowledgeBaseWriter>();
                s.AddSingleton<IKnowledgeBaseWriter>(recorder);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);

        await client.PutAsJsonAsync("/reports/daily-2026-07-10", DraftBody());
        await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 });
        // 確定済みの再確定（冪等・遷移なし）。
        await client.PostAsJsonAsync("/reports/daily-2026-07-10/confirm", new { ExpectedVersion = 1 });

        recorder.Saved.Should().ContainSingle();
    }

    private sealed class RecordingKnowledgeBaseWriter : IKnowledgeBaseWriter
    {
        public List<KnowledgeDocument> Saved { get; } = [];

        public Task<KnowledgeWriteResult> SaveAsync(KnowledgeDocument document, CancellationToken cancellationToken = default)
        {
            Saved.Add(document);
            return Task.FromResult(KnowledgeWriteResult.Ok(Guid.NewGuid()));
        }
    }

    private sealed record ReviewDto(string PeriodKey, string State, int Version);

    private sealed record BootstrapDto(string Kind, string PeriodKey, string PolicySummary);

    private sealed record DailyPolicyDto(DateOnly Date, string Summary, int AssumptionsVersion);

    private sealed record PnlSummaryDto(decimal RealizedPnlGross, decimal RealizedPnlNet, int RealizingTradeCount);

    private sealed record DraftDto(string Markdown, PnlSummaryDto Pnl);
}
