using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Features.Reports.ConfirmReport;
using ReportService.Features.Reports.RevisePolicy;
using ReportService.Features.Reports.WatchlistProposal;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Tests;

// T-10-1733（提供側）, NFR, NFR-06, FR-07, FR-09, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0450 決定 2・3, #753:
// Discord ボットが呼ぶ報告書の**所有者限定の書き込みの gRPC 面**（`ReportOwnerWrite` の 4 rpc）。REST の端点と**同じ処理関数**を通ること
// （同じ状態の変化・同じ確定者〔on_behalf_of は信頼クライアントに限る〕・同じ拒否の分類と文言）と、門が `GrpcOwnerOnly` であることを、
// 本物の Program.cs（ReportWorkerWebApplicationFactory）で固定する。あわせて PR #1069 の監査（入れ替え案の保存 JSON の null 要素で
// INTERNAL になっていた）を直したことを固定する。
public class ReportOwnerWriteGrpcServiceTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private const string Key = "daily-2026-09-10";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Trusted(ReportWorkerWebApplicationFactory baseFactory) =>
        baseFactory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DelegatedActorOptions.TrustedClientIdsKey] = Bot,
            })));

    // ボットのトークン（名前クレーム無し・azp はボットの機密クライアント）。
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

    private static Proto.ReportOwnerWrite.ReportOwnerWriteClient Grpc(WebApplicationFactory<Program> factory, string? roles = OwnerRole, string? azp = Bot) =>
        new(GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new Headers(factory.Server.CreateHandler(), roles, azp) }));

    private static HttpClient Rest(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        client.DefaultRequestHeaders.Add(TestAuthHandler.NameHeader, TestAuthHandler.NoName);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AzpHeader, Bot);
        return client;
    }

    private static async Task<(int Status, string? Error)> PostAsync(HttpClient rest, string path, object body)
    {
        using var response = await rest.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        string? error = null;
        if (!response.IsSuccessStatusCode && text.Length > 0)
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var e))
                error = e.GetString();
        }

        return ((int)response.StatusCode, error);
    }

    private static async Task<RpcException> FailsAsync(Func<Task> act) => (await act.Should().ThrowAsync<RpcException>()).Which;

    // 版 1 のドラフトを作る。confirm なら版 1 で確定する（確定後の版は 2）。
    private static void SeedReport(WebApplicationFactory<Program> factory, bool confirm = false)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = Key,
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 10),
            AssumptionsVersion = 1,
            PolicySummary = "押し目買いを優先する",
            Body = "# 日報\n",
        }, 0);
        if (confirm)
            store.Confirm(Key, 1, DateTimeOffset.UtcNow).Should().NotBeNull();
    }

    private static Guid SeedProposal(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPolicyRevisionLedger>();
        var id = ledger.Begin(new PolicyRevisionAttempt(
            Guid.NewGuid(), DateTimeOffset.UtcNow, new DateOnly(2026, 9, 10), "owner", Key, WatchlistSnapshotJson: """[{"symbol":"MSFT","market":"UnitedStates"}]"""));
        ledger.Complete(id, PolicyRevisionAttemptOutcome.Proposed, 1, """[{"action":"add","symbol":"NVDA","reason":"出来高"}]""");
        return id;
    }

    private static ReportState StateOf(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IReportStore>().Get(Key)!.Report.State;
    }

    // ---- 確定 ----

    [Fact]
    public async Task T_10_1733_確定は_REST_と同じ遷移と版を返し再確定は冪等()
    {
        await using var restBase = new ReportWorkerWebApplicationFactory();
        await using var grpcBase = new ReportWorkerWebApplicationFactory();
        var restHost = Trusted(restBase);
        var grpcHost = Trusted(grpcBase);
        SeedReport(restHost);
        SeedReport(grpcHost);

        foreach (var attempt in new[] { "初回", "再確定" })
        {
            using var response = await Rest(restHost).PostAsJsonAsync($"/reports/{Key}/confirm", new { expectedVersion = 1, onBehalfOf = "owner-a" });
            var rest = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
            var grpc = await Grpc(grpcHost).ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1, OnBehalfOf = "owner-a" });

            response.StatusCode.Should().Be(HttpStatusCode.OK, attempt);
            (grpc.HasTransitioned, grpc.Transitioned, grpc.HasVersion, grpc.Version)
                .Should().Be((true, rest.GetProperty("transitioned").GetBoolean(), true, rest.GetProperty("version").GetInt32()), attempt);
        }

        StateOf(grpcHost).Should().Be(StateOf(restHost)).And.Be(ReportState.Confirmed);
    }

    [Theory]
    [InlineData(5, 409, StatusCode.Aborted)]         // 版の不一致
    public async Task T_10_1733_確定の版の不一致は_REST_の_409_と同じ文言の_ABORTED(int expectedVersion, int restStatus, StatusCode grpcStatus)
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);
        SeedReport(factory);

        var (status, error) = await PostAsync(Rest(factory), $"/reports/{Key}/confirm", new { expectedVersion, onBehalfOf = "owner-a" });
        var ex = await FailsAsync(async () => await Grpc(factory).ConfirmReportAsync(
            new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = expectedVersion, OnBehalfOf = "owner-a" }));

        status.Should().Be(restStatus);
        (ex.StatusCode, ex.Status.Detail).Should().Be((grpcStatus, error!));
        StateOf(factory).Should().NotBe(ReportState.Confirmed);
    }

    [Fact]
    public async Task T_10_1733_確定の対象が無い_代理の値域外_空の会話キーは_REST_と同じ分類()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);
        SeedReport(factory);

        (await PostAsync(Rest(factory), "/reports/daily-2099-01-01/confirm", new { expectedVersion = 1, onBehalfOf = "owner-a" })).Status.Should().Be(404);
        (await FailsAsync(async () => await Grpc(factory).ConfirmReportAsync(
            new Proto.ReportConfirmationRequest { PeriodKey = "daily-2099-01-01", ExpectedVersion = 1, OnBehalfOf = "owner-a" }))).StatusCode
            .Should().Be(StatusCode.NotFound);

        var (status, error) = await PostAsync(Rest(factory), $"/reports/{Key}/confirm", new { expectedVersion = 1, onBehalfOf = "bad name\n" });
        var ex = await FailsAsync(async () => await Grpc(factory).ConfirmReportAsync(
            new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1, OnBehalfOf = "bad name\n" }));
        status.Should().Be(400);
        (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!), "確定者を記録できない確定は行わない");

        (await FailsAsync(async () => await Grpc(factory).ConfirmReportAsync(new Proto.ReportConfirmationRequest { ExpectedVersion = 1 }))).StatusCode
            .Should().Be(StatusCode.InvalidArgument, "REST の経路引数は空になり得ない。gRPC では入力の誤り");
        StateOf(factory).Should().NotBe(ReportState.Confirmed);
    }

    // ---- 差し戻し ----

    [Fact]
    public async Task T_10_1733_差し戻しは_REST_と同じ版を返し不正な遷移は同じ文言の_ABORTED()
    {
        await using var restHost = new ReportWorkerWebApplicationFactory();
        await using var grpcHost = new ReportWorkerWebApplicationFactory();
        SeedReport(restHost);
        SeedReport(grpcHost);

        // 提示の前（ドラフト）の差し戻しは不正な遷移（REST の 409）。
        var (status, error) = await PostAsync(Rest(restHost), $"/reports/{Key}/request-changes", new { expectedVersion = 1 });
        var ex = await FailsAsync(async () => await Grpc(grpcHost).RequestReportChangesAsync(new Proto.ReportChangesRequest { PeriodKey = Key, ExpectedVersion = 1 }));
        status.Should().Be(409);
        (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.Aborted, error!));

        foreach (var host in new[] { restHost, grpcHost })
            (await Rest(host).PostAsJsonAsync($"/reports/{Key}/present", new { expectedVersion = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await Rest(restHost).PostAsJsonAsync($"/reports/{Key}/request-changes", new { expectedVersion = 1 });
        var rest = await response.Content.ReadFromJsonAsync<JsonElement>(Web);
        var grpc = await Grpc(grpcHost).RequestReportChangesAsync(new Proto.ReportChangesRequest { PeriodKey = Key, ExpectedVersion = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (grpc.HasVersion, grpc.Version).Should().Be((true, rest.GetProperty("version").GetInt32()));
        using var scope = grpcHost.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IReportStore>().GetReview(Key)!.State.Should().Be(ReviewState.ChangesRequested);
    }

    // ---- 方針の改訂: 入力の誤りは REST の 400 と同じ文言（LLM を呼ぶ前に止まる） ----

    [Fact]
    public async Task T_10_1733_方針の改訂の入力の誤りは_REST_の_400_と同じ文言の_INVALID_ARGUMENT()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);

        var cases = new (object RestBody, Proto.PolicyRevisionProposalRequest GrpcBody)[]
        {
            (new { instruction = "", onBehalfOf = "owner-a" }, new Proto.PolicyRevisionProposalRequest { OnBehalfOf = "owner-a" }),
            (new { instruction = "もっと慎重に", onBehalfOf = "bad name\n" },
                new Proto.PolicyRevisionProposalRequest { Instruction = "もっと慎重に", OnBehalfOf = "bad name\n" }),
            (new { instruction = "もっと慎重に", periodKey = "bad key!", onBehalfOf = "owner-a" },
                new Proto.PolicyRevisionProposalRequest { Instruction = "もっと慎重に", PeriodKey = "bad key!", OnBehalfOf = "owner-a" }),
        };
        foreach (var (restBody, grpcBody) in cases)
        {
            var (status, error) = await PostAsync(Rest(factory), "/reports/policy-revisions", restBody);
            var ex = await FailsAsync(async () => await Grpc(factory).RevisePolicyAsync(grpcBody));

            status.Should().Be(400);
            error.Should().NotBeNullOrEmpty();
            (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!));
        }
    }

    // ---- 適用の内訳の記録: 1 回だけ・確定された案だけ ----

    [Fact]
    public async Task T_10_1733_適用の内訳の記録は_REST_と同じく1回だけ受け付ける()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);
        SeedReport(factory, confirm: true);
        var attemptId = SeedProposal(factory);
        var request = new Proto.WatchlistApplyRecordRequest
        {
            AttemptId = attemptId.ToString(),
            Outcome = "applied",
            Message = "適用しました",
            OnBehalfOf = "owner-a",
            Items = { new Proto.WatchlistApplyRecordItem { Action = "add", Symbol = "NVDA", Applied = true } },
        };

        var recorded = await Grpc(factory).RecordWatchlistApplyResultAsync(request);
        recorded.AttemptId.Should().Be(attemptId.ToString());

        // 2 回目は REST と同じ 409 の文言の ABORTED（書くとその案の適用を永久に塞ぐので 1 回だけ）。
        var (status, error) = await PostAsync(Rest(factory), $"/reports/policy-revisions/{attemptId}/watchlist-apply-result",
            new { outcome = "applied", items = Array.Empty<object>(), message = "m", onBehalfOf = "owner-a" });
        var again = await FailsAsync(async () => await Grpc(factory).RecordWatchlistApplyResultAsync(request));
        status.Should().Be(409);
        (again.StatusCode, again.Status.Detail).Should().Be((StatusCode.Aborted, error!));

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPolicyRevisionLedger>().Find(attemptId)!.WatchlistAppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task T_10_1733_適用の内訳の記録の誤りは_REST_と同じ分類()
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);
        var unknown = Guid.NewGuid();

        (await PostAsync(Rest(factory), $"/reports/policy-revisions/{unknown}/watchlist-apply-result",
            new { outcome = "applied", items = Array.Empty<object>(), message = "m", onBehalfOf = "owner-a" })).Status.Should().Be(404);
        (await FailsAsync(async () => await Grpc(factory).RecordWatchlistApplyResultAsync(new Proto.WatchlistApplyRecordRequest
        {
            AttemptId = unknown.ToString(),
            Outcome = "applied",
            OnBehalfOf = "owner-a",
        }))).StatusCode.Should().Be(StatusCode.NotFound);

        var (status, error) = await PostAsync(Rest(factory), $"/reports/policy-revisions/{unknown}/watchlist-apply-result",
            new { outcome = "", items = Array.Empty<object>(), message = "m", onBehalfOf = "owner-a" });
        var ex = await FailsAsync(async () => await Grpc(factory).RecordWatchlistApplyResultAsync(new Proto.WatchlistApplyRecordRequest
        {
            AttemptId = unknown.ToString(),
            Outcome = "",
            OnBehalfOf = "owner-a",
        }));
        status.Should().Be(400);
        (ex.StatusCode, ex.Status.Detail).Should().Be((StatusCode.InvalidArgument, error!));

        (await FailsAsync(async () => await Grpc(factory).RecordWatchlistApplyResultAsync(new Proto.WatchlistApplyRecordRequest
        {
            AttemptId = "not-a-guid",
            Outcome = "applied",
        }))).StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ---- 門（GrpcOwnerOnly）: 5 rpc すべて ----

    private static Func<Task>[] AllRpcs(Proto.ReportOwnerWrite.ReportOwnerWriteClient c) =>
    [
        async () => await c.ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = Key, ExpectedVersion = 1, OnBehalfOf = "owner-a" }),
        async () => await c.RequestReportChangesAsync(new Proto.ReportChangesRequest { PeriodKey = Key, ExpectedVersion = 1 }),
        async () => await c.RevisePolicyAsync(new Proto.PolicyRevisionProposalRequest { Instruction = "i", OnBehalfOf = "owner-a" }),
        async () => await c.RecordWatchlistApplyResultAsync(new Proto.WatchlistApplyRecordRequest { AttemptId = Guid.NewGuid().ToString(), Outcome = "applied" }),
        // T-10-2271, FR-06, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 1: 作り直しも同じ所有者の門（ボットだけ・s2s には開かない）。
        async () => await c.RegenerateReportAsync(new Proto.ReportRegenerationRequest { PeriodKey = Key, OnBehalfOf = "owner-a" }),
    ];

    [Theory]
    [InlineData(OwnerRole, null)]
    [InlineData(OwnerRole, "ai-stock-trading-dev")]
    [InlineData(OwnerRole, "bff")]
    [InlineData(ServiceRole, Bot)]                    // 🔴 s2s には開かない（生成AI・自動処理は確定できない＝ADR-0003）
    [InlineData(ServiceRole, "ai-stock-trading-svc")]
    public async Task T_10_1733_書き込みの面はボット以外を_PERMISSION_DENIED(string roles, string? azp)
    {
        await using var baseFactory = new ReportWorkerWebApplicationFactory();
        var factory = Trusted(baseFactory);
        SeedReport(factory);

        foreach (var act in AllRpcs(Grpc(factory, roles, azp)))
            (await FailsAsync(act)).StatusCode.Should().Be(StatusCode.PermissionDenied);
        StateOf(factory).Should().NotBe(ReportState.Confirmed, "拒否では確定しない");
    }

    [Fact]
    public async Task T_10_1733_資格情報が無ければ_UNAUTHENTICATED()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();

        foreach (var act in AllRpcs(Grpc(factory, roles: null, azp: null)))
            (await FailsAsync(act)).StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    // ---- PR #1069 の監査: 入れ替え案の保存 JSON の null 要素 ----

    // 🔴 null 要素で NRE → INTERNAL にしない。空の行として運び、受け手が REST と同じに読めるようにする
    // （変更の空の行＝案ごと解釈できない、スナップショットの空の行＝一覧ごと「分からない」）。
    [Fact]
    public void T_10_1733_入れ替え案の保存_JSON_の_null_要素は空の行として運ぶ()
    {
        var view = new WatchlistProposalView(
            Guid.NewGuid(), Key, 1,
            [new WatchlistChangeView("add", "NVDA", "r"), null!],
            [new WatchlistSnapshotEntryView("MSFT", "UnitedStates"), null!],
            false);

        var proto = ReportOwnerReadWireMapping.ToProto(view);

        proto.Changes.Should().HaveCount(2);
        (proto.Changes[1].HasAction, proto.Changes[1].HasSymbol).Should().Be((false, false));
        proto.Snapshot.Items.Should().HaveCount(2);
        (proto.Snapshot.Items[1].HasSymbol, proto.Snapshot.Items[1].HasMarket).Should().Be((false, false));
    }

    [Fact]
    public async Task T_10_1733_入れ替え案の保存_JSON_の_null_要素でも照会は_INTERNAL_にならない()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        SeedReport(factory, confirm: true);
        using (var scope = factory.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IPolicyRevisionLedger>();
            var id = ledger.Begin(new PolicyRevisionAttempt(
                Guid.NewGuid(), DateTimeOffset.UtcNow, new DateOnly(2026, 9, 10), "owner", Key, WatchlistSnapshotJson: """[{"symbol":"MSFT","market":"UnitedStates"},null]"""));
            ledger.Complete(id, PolicyRevisionAttemptOutcome.Proposed, 1, """[{"action":"add","symbol":"NVDA","reason":"r"},null]""");
        }

        var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new Headers(factory.Server.CreateHandler(), OwnerRole, Bot) });
        var proposal = await new Proto.ReportOwnerRead.ReportOwnerReadClient(channel)
            .GetWatchlistProposalAsync(new Proto.GetWatchlistProposalRequest { PeriodKey = Key, Version = 1 });

        proposal.Changes.Should().HaveCount(2);
        proposal.Snapshot.Items.Should().HaveCount(2);
    }
}
