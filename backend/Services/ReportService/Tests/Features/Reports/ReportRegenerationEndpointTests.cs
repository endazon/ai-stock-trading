using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Features.Reports.ConfirmReport;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Tests;

// T-10-2271, T-10-2272, FR-06, FR-14, UC-03〜05, 計画 ADR-0052 決定 1・4・5, #1156, IADR-0491 決定 1: 作り直しの REST（`POST /reports/{periodKey}/regenerate`）
// と gRPC（`ReportOwnerWrite/RegenerateReport`）の面を、本物の Program.cs で固定する。所有者の門（OwnerOnly・s2s には開かない）・作り直した
// 利用者（onBehalfOf は信頼クライアントに限る）・拒否の分類（422＝中核の入力・429＝上限・409＝確定済み）と gRPC の写し。
public class ReportRegenerationEndpointTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private const string Key = "daily-2026-09-10";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class SuppliedDrift : IPeriodDriftAdoptionSource
    {
        public Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PeriodDriftAdoption>?>([]);
    }

    // FR-06, #1181, IADR-0493 決定 4: 期間開始時点の在庫も中核の入力である（既定の構成はリスク管理の所在が未構成＝未供給）。
    private sealed class SuppliedOpening : IOpeningInventorySource
    {
        public Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
            AiStockTrading.Shared.Contracts.Trading.Market market, DateOnly beforeTradingDay,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OpeningLot>?>([]);
    }

    // 信頼クライアント（ボット）・中核の入力の供給（既定の構成は手動売買の取り込み・期間開始時点の在庫が未構成＝未供給）・上限。
    private static WebApplicationFactory<Program> Host(ReportWorkerWebApplicationFactory baseFactory, bool coreSupplied = true, int? limit = null) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DelegatedActorOptions.TrustedClientIdsKey] = Bot,
                [ReportRegenerationLimit.ConfigKey] = limit?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
            if (coreSupplied)
                b.ConfigureTestServices(s =>
                {
                    s.Replace(ServiceDescriptor.Singleton<IPeriodDriftAdoptionSource>(new SuppliedDrift()));
                    s.Replace(ServiceDescriptor.Singleton<IOpeningInventorySource>(new SuppliedOpening()));
                });
        });

    private sealed class Headers(HttpMessageHandler inner, string? roles, string? azp) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (roles is not null)
            {
                request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
                request.Headers.TryAddWithoutValidation(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
            }

            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static Proto.ReportOwnerWrite.ReportOwnerWriteClient Grpc(WebApplicationFactory<Program> factory) =>
        new(GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new Headers(factory.Server.CreateHandler(), OwnerRole, Bot) }));

    private static HttpClient Rest(WebApplicationFactory<Program> factory, string? roles = OwnerRole, string? azp = Bot)
    {
        var client = factory.CreateClient();
        if (roles is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
            client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        }

        if (azp is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, azp);
        return client;
    }

    private static int Seed(WebApplicationFactory<Program> factory, bool confirm = false)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        var version = store.UpsertDraft(new TradingReport
        {
            PeriodKey = Key,
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 10),
            AssumptionsVersion = 1,
            PolicySummary = "押し目買いを優先する",
            Body = "# 日報\n縮退した散文\n",
        }, 0);
        if (confirm)
            store.Confirm(Key, version, DateTimeOffset.UtcNow).Should().NotBeNull();
        return version;
    }

    private static (int Version, string Policy, string Body) Saved(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<IReportStore>().Get(Key)!;
        return (row.Version, row.Report.PolicySummary, row.Report.Body);
    }

    private static IReadOnlyList<ReportRegenerationAttempt> Attempts(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportService.Infrastructure.Persistence.ReportDbContext>();
        return [.. db.ReportRegenerationAttempts.ToList().Select(r => new ReportRegenerationAttempt(
            r.Id, r.AttemptedAt, r.JstDate, r.Actor, r.PeriodKey, r.PreviousVersion, r.Outcome, r.ReportVersion, r.UnsuppliedInputs, r.NotRestorableInputs))];
    }

    // T-10-2271, 計画 ADR-0052 決定 1・5: REST。所有者（ボット＋代理の利用者）が作り直すと 200・版を上げ、方針は変わらず、作り直した利用者は
    // onBehalfOf の利用者として台帳に残る。
    [Fact]
    public async Task REST_所有者の作り直しは版を上げ代理の利用者を記録する()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Host(baseFactory);
        var before = Seed(factory);

        using var response = await Rest(factory).PostAsJsonAsync($"/reports/{Key}/regenerate", new { onBehalfOf = "owner-a" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Web);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.GetProperty("periodKey").GetString().Should().Be(Key);
        body.GetProperty("previousVersion").GetInt32().Should().Be(before);
        body.GetProperty("version").GetInt32().Should().Be(before + 1);
        body.GetProperty("message").GetString().Should().Contain("/report approve " + Key);
        var saved = Saved(factory);
        saved.Version.Should().Be(before + 1);
        saved.Policy.Should().Be("押し目買いを優先する");
        saved.Body.Should().Contain("- 作り直した利用者: owner-a").And.NotContain("縮退した散文");
        Attempts(factory).Should().ContainSingle(a => a.Actor == "owner-a" && a.Outcome == ReportRegenerationOutcome.Regenerated);
    }

    // T-10-2271（否定形）, 計画 ADR-0052 決定 1: 所有者の門。s2s（trading-service）は 403・資格情報なしは 401。どちらも下書きは変わらない。
    [Theory]
    [InlineData(ServiceRole, HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task REST_所有者以外は作り直せない(string? roles, HttpStatusCode expected)
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Host(baseFactory);
        var before = Seed(factory);

        using var response = await Rest(factory, roles, azp: roles is null ? null : "ai-stock-trading-svc")
            .PostAsJsonAsync($"/reports/{Key}/regenerate", new { onBehalfOf = "owner-a" });

        response.StatusCode.Should().Be(expected);
        Saved(factory).Version.Should().Be(before);
        Attempts(factory).Should().BeEmpty();
    }

    // T-10-2272, 計画 ADR-0052 決定 4: 中核の入力が未供給（既定の構成は手動売買の取り込みが未構成）なら REST は 422、gRPC は FAILED_PRECONDITION。
    // 理由（入力の表示名）を返し、下書きは変わらない。
    [Fact]
    public async Task 中核の入力が未供給なら_REST_422_gRPC_FAILED_PRECONDITION()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Host(baseFactory, coreSupplied: false);
        var before = Seed(factory);

        using var response = await Rest(factory).PostAsJsonAsync($"/reports/{Key}/regenerate", new { onBehalfOf = "owner-a" });
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("error").GetString();
        var rpc = (await FluentRpc(() => Grpc(factory).RegenerateReportAsync(
            new Proto.ReportRegenerationRequest { PeriodKey = Key, OnBehalfOf = "owner-a" }).ResponseAsync));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        error.Should().Contain("手動売買の取り込み").And.Contain("回数は消費していません");
        (rpc.StatusCode, rpc.Status.Detail).Should().Be((StatusCode.FailedPrecondition, error!));
        Saved(factory).Version.Should().Be(before);
    }

    // T-10-2272, 計画 ADR-0052 決定 1: 上限に達したら REST は 429、gRPC は RESOURCE_EXHAUSTED。確定済みは REST 409・gRPC ABORTED。
    [Fact]
    public async Task 上限は_429_RESOURCE_EXHAUSTED_確定済みは_409_ABORTED()
    {
        await using var limitedBase = new ReportWorkerWebApplicationFactory();
        var limited = Host(limitedBase, limit: 1);
        Seed(limited);
        (await Grpc(limited).RegenerateReportAsync(new Proto.ReportRegenerationRequest { PeriodKey = Key, OnBehalfOf = "owner-a" }))
            .Version.Should().Be(2);

        using var second = await Rest(limited).PostAsJsonAsync($"/reports/{Key}/regenerate", new { onBehalfOf = "owner-a" });
        var exhausted = await FluentRpc(() => Grpc(limited).RegenerateReportAsync(
            new Proto.ReportRegenerationRequest { PeriodKey = Key, OnBehalfOf = "owner-a" }).ResponseAsync);

        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        exhausted.StatusCode.Should().Be(StatusCode.ResourceExhausted);

        await using var confirmedBase = new ReportWorkerWebApplicationFactory();
        var confirmed = Host(confirmedBase);
        Seed(confirmed, confirm: true);
        using var conflict = await Rest(confirmed).PostAsJsonAsync($"/reports/{Key}/regenerate", new { onBehalfOf = "owner-a" });
        var aborted = await FluentRpc(() => Grpc(confirmed).RegenerateReportAsync(
            new Proto.ReportRegenerationRequest { PeriodKey = Key, OnBehalfOf = "owner-a" }).ResponseAsync);

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        aborted.StatusCode.Should().Be(StatusCode.Aborted);
    }

    // T-10-2272: gRPC の成功の写し（版・案内文・未供給の表示名）。期間が過ぎた日報なので建玉は復元できない入力として返る。
    [Fact]
    public async Task gRPC_の成功は版と未供給の表示名を写す()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Host(baseFactory);
        var before = Seed(factory);

        var reply = await Grpc(factory).RegenerateReportAsync(new Proto.ReportRegenerationRequest { PeriodKey = Key, OnBehalfOf = "owner-a" });

        (reply.PeriodKey, reply.PreviousVersion, reply.Version).Should().Be((Key, before, before + 1));
        reply.Message.Should().Contain($"版 {before + 1}");
        reply.NotRestorableInputs.Should().Equal(ReportInputs.Label(ReportInput.OpenPositions));
        reply.UnsuppliedInputs.Should().Contain(ReportInputs.Label(ReportInput.OpenPositions));
    }

    // T-10-2265, IADR-0491 決定 5: 本番の組み立ては監査をバスへ出す本物の発行口と EF の台帳を選ぶ。
    [Fact]
    public async Task 本番の組み立ては監査の発行口と台帳の本物を選ぶ()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        factory.Services.GetRequiredService<IReportRegenerationAuditPublisher>()
            .Should().BeOfType<ReportService.Infrastructure.ExternalServices.MessageBusReportRegenerationAuditPublisher>();
        scope.ServiceProvider.GetRequiredService<IReportRegenerationLedger>()
            .Should().BeOfType<ReportService.Infrastructure.Persistence.EfReportRegenerationLedger>();
        factory.Services.GetRequiredService<ReportRegenerationLimit>().DailyLimit.Should().Be(ReportRegenerationLimit.DefaultDailyLimit);
    }

    private static async Task<RpcException> FluentRpc(Func<Task> act) => (await act.Should().ThrowAsync<RpcException>()).Which;
}
