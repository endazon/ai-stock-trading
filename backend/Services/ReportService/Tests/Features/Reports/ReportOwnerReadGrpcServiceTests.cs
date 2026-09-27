using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReportService.Domain;
using ReportService.Features.Reports;
using ReportService.Features.Reports.WatchlistProposal;
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Tests;

// T-10-1729（提供側）, NFR, NFR-06, FR-07, FR-13, FR-14, MSP:ADR-0029, ADR-0047 決定 1〜3, IADR-0284 決定 5（段 5）, IADR-0449 決定 2・3, #753:
// Discord ボットが読む報告書の**所有者限定の読み取りの gRPC 面**（`ReportOwnerRead`）。REST（OwnerOnly の 3 本）と**同じ値・同じ誤りの分類**で
// あり、門は `GrpcOwnerOnly`（ボット可・s2s 不可・人の利用者不可）であることを、本物の Program.cs（ReportWorkerWebApplicationFactory）で固定する。
// 🔴 本物の Program.cs で呼べること自体が `MapGrpcService` の登録の証拠である（外すと UNIMPLEMENTED）。
public class ReportOwnerReadGrpcServiceTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private const string Key = "daily-2026-09-10";

    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient Rest(ReportWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, OwnerRole);
        return client;
    }

    private static Proto.ReportOwnerRead.ReportOwnerReadClient Bot_(GrpcChannel channel) => new(channel);

    // 版 1 のドラフトを作る（未供給の入力を 2 つ持つ）。confirm なら版 1 で確定する（確定後の版は 2）。
    private static void SeedReport(ReportWorkerWebApplicationFactory factory, string key, DateOnly start, bool confirm)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = key,
            Kind = ReportKind.Daily,
            PeriodStart = start,
            AssumptionsVersion = 1,
            PolicySummary = "押し目買いを優先する",
            Body = "# 日報\n",
            UnsuppliedInputs = [ReportInput.Fills, ReportInput.Fills + 1],
        }, 0);
        if (confirm)
            store.Confirm(key, 1, DateTimeOffset.UtcNow).Should().NotBeNull();
    }

    // 版 1 の案（入れ替え 2 件・案を作った時点の監視銘柄 snapshotJson）を台帳へ記録する。
    private static Guid SeedProposal(ReportWorkerWebApplicationFactory factory, string? snapshotJson)
    {
        using var scope = factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IPolicyRevisionLedger>();
        var id = ledger.Begin(new PolicyRevisionAttempt(
            Guid.NewGuid(), DateTimeOffset.UtcNow, new DateOnly(2026, 9, 10), "owner", Key, WatchlistSnapshotJson: snapshotJson));
        ledger.Complete(id, PolicyRevisionAttemptOutcome.Proposed, 1,
            """[{"action":"add","symbol":"NVDA","reason":"出来高が増えた"},{"action":"remove","symbol":"7203","reason":"材料が出尽くした"}]""");
        return id;
    }

    [Fact]
    public async Task T_10_1729_レビュー局面は_REST_と同じ値を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        SeedReport(factory, Key, new DateOnly(2026, 9, 10), confirm: false);
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        var restView = await rest.GetFromJsonAsync<ReportReviewView>($"/reports/{Key}/review", ReportWire);
        var grpc = await Bot_(channel).GetReportReviewAsync(new Proto.GetReportReviewRequest { PeriodKey = Key });

        restView!.UnsuppliedInputs.Should().HaveCount(2, "空どうしの一致は何も証明しない");
        grpc.Should().Be(ReportOwnerReadWireMapping.ToProto(restView));
        grpc.Version.Should().Be(restView.Version);
        grpc.UnsuppliedInputs.Names.Should().Equal(restView.UnsuppliedInputs);
    }

    [Fact]
    public async Task T_10_1729_会話キーの一覧は_REST_と同じ値と順を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        SeedReport(factory, "daily-2026-09-09", new DateOnly(2026, 9, 9), confirm: false);
        SeedReport(factory, Key, new DateOnly(2026, 9, 10), confirm: false);
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        var restItems = await rest.GetFromJsonAsync<List<ReportPeriodKeyItem>>("/reports/period-keys", ReportWire);
        var grpc = await Bot_(channel).ListReportPeriodKeysAsync(new Proto.ListReportPeriodKeysRequest());

        restItems.Should().HaveCount(2);
        grpc.Items.Should().Equal(restItems!.Select(ReportOwnerReadWireMapping.ToProto));
        grpc.Items[0].PeriodKey.Should().Be(Key, "新しい順");
        grpc.Items[0].PeriodStart.Should().Be("2026-09-10");
    }

    [Theory]
    [InlineData("有り")]
    [InlineData("不明")]
    public async Task T_10_1729_入れ替え案は_REST_と同じ値を返し不明な監視銘柄は入れ物ごと運ばない(string snapshot)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        SeedReport(factory, Key, new DateOnly(2026, 9, 10), confirm: true);
        var id = SeedProposal(factory, snapshot == "有り" ? """[{"symbol":"7203","market":"Japan"}]""" : null);
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        var restView = await rest.GetFromJsonAsync<WatchlistProposalView>(
            $"/reports/policy-revisions/watchlist-proposal?periodKey={Key}&version=1", ReportWire);
        var grpc = await Bot_(channel).GetWatchlistProposalAsync(new Proto.GetWatchlistProposalRequest { PeriodKey = Key, Version = 1 });

        restView!.Changes.Should().HaveCount(2, "空どうしの一致は何も証明しない");
        grpc.Should().Be(ReportOwnerReadWireMapping.ToProto(restView));
        grpc.AttemptId.Should().Be(id.ToString());
        grpc.HasApplyRecorded.Should().BeTrue("false も値として運ぶ");
        if (snapshot == "有り")
            grpc.Snapshot.Items.Should().ContainSingle(e => e.Symbol == "7203" && e.Market == "Japan");
        else
            grpc.Snapshot.Should().BeNull("分からない（null）を空の一覧と区別する");
    }

    // REST の 400 / 404 / 409 と同じ分類（入れ替え案の判定は 1 つ＝ WatchlistProposalEndpoints.Lookup）。
    [Theory]
    [InlineData("daily-2026-09-10", 0, HttpStatusCode.BadRequest, StatusCode.InvalidArgument)]      // 版の誤り
    [InlineData("bad key!", 1, HttpStatusCode.BadRequest, StatusCode.InvalidArgument)]             // 会話キーの誤り
    [InlineData("daily-2026-09-10", 7, HttpStatusCode.NotFound, StatusCode.NotFound)]              // 案ではない
    [InlineData("daily-2026-09-10", 1, HttpStatusCode.Conflict, StatusCode.FailedPrecondition)]    // その版で確定されていない
    public async Task T_10_1729_入れ替え案の誤りは_REST_と同じ分類(string key, int version, HttpStatusCode restStatus, StatusCode grpcStatus)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        SeedReport(factory, Key, new DateOnly(2026, 9, 10), confirm: false);
        SeedProposal(factory, null);
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        (await rest.GetAsync($"/reports/policy-revisions/watchlist-proposal?periodKey={Uri.EscapeDataString(key)}&version={version}"))
            .StatusCode.Should().Be(restStatus);
        var act = async () => await Bot_(channel).GetWatchlistProposalAsync(
            new Proto.GetWatchlistProposalRequest { PeriodKey = key, Version = version });
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(grpcStatus);
    }

    [Fact]
    public async Task T_10_1729_レビュー局面の対象が無ければ_NOT_FOUND_空の会話キーは_INVALID_ARGUMENT()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, OwnerRole, Bot);

        (await rest.GetAsync("/reports/daily-2099-01-01/review")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var missing = async () => await Bot_(channel).GetReportReviewAsync(new Proto.GetReportReviewRequest { PeriodKey = "daily-2099-01-01" });
        (await missing.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);

        var empty = async () => await Bot_(channel).GetReportReviewAsync(new Proto.GetReportReviewRequest());
        (await empty.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    // ---- 門（GrpcOwnerOnly）: ボットだけが通る。3 つの rpc すべて ----

    [Theory]
    [InlineData(OwnerRole, null)]
    [InlineData(OwnerRole, "ai-stock-trading-dev")]
    [InlineData(OwnerRole, "bff")]
    [InlineData(OwnerRole, "ai-stock-trading-owner-bff")]
    [InlineData(ServiceRole, Bot)]                    // 🔴 s2s には開かない（REST の OwnerOnly と同じ）
    [InlineData(ServiceRole, "ai-stock-trading-svc")]
    public async Task T_10_1729_所有者限定の面はボット以外を_PERMISSION_DENIED(string roles, string? azp)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, roles, azp);
        var client = Bot_(channel);

        foreach (var act in new Func<Task>[]
        {
            async () => await client.GetReportReviewAsync(new Proto.GetReportReviewRequest { PeriodKey = Key }),
            async () => await client.ListReportPeriodKeysAsync(new Proto.ListReportPeriodKeysRequest()),
            async () => await client.GetWatchlistProposalAsync(new Proto.GetWatchlistProposalRequest { PeriodKey = Key, Version = 1 }),
        })
        {
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        }
    }

    [Fact]
    public async Task T_10_1729_資格情報が無ければ_UNAUTHENTICATED_REST_の面は変わらない()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        SeedReport(factory, Key, new DateOnly(2026, 9, 10), confirm: false);
        using var anonymous = ChannelFor(factory, roles: null);

        var act = async () => await Bot_(anonymous).ListReportPeriodKeysAsync(new Proto.ListReportPeriodKeysRequest());
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);

        // REST の面: azp の無い所有者は従来どおり読める。
        using var rest = Rest(factory);
        (await rest.GetAsync($"/reports/{Key}/review")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await rest.GetAsync("/reports/period-keys")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
