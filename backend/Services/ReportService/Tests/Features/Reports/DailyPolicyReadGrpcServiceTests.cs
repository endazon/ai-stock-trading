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
using Xunit;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;

namespace ReportService.Tests;

// T-10-1690（提供側）, NFR, FR-04, FR-07, MSP:ADR-0029, IADR-0284 決定 5（段 4）, IADR-0446 決定 2・3, #1061 (#753):
// 確定済み日報の方針の **gRPC 面**。REST（`GET /reports/daily-policy`）と**同じ値・同じ認可**であることを、本物の Program.cs
// （ReportWorkerWebApplicationFactory）で固定する。REST の本文を送り手の型へ戻して提供側の写しで proto にしたものと、gRPC の応答を
// message ごと等価比較する。🔴 本物の Program.cs で呼べること自体が `MapGrpcService` の登録の証拠である（外すと UNIMPLEMENTED）。
public class DailyPolicyReadGrpcServiceTests
{
    private const string ServiceRole = "trading-service";
    private static readonly JsonSerializerOptions ReportWire =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static HttpClient Rest(ReportWorkerWebApplicationFactory factory, string roles = ServiceRole)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static void ConfirmDaily(ReportWorkerWebApplicationFactory factory, string summary)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReportStore>();
        store.UpsertDraft(new TradingReport
        {
            PeriodKey = "daily-2026-09-10",
            Kind = ReportKind.Daily,
            PeriodStart = new DateOnly(2026, 9, 10),
            AssumptionsVersion = 1,
            PolicySummary = summary,
            Body = "# 日報\n",
        }, 0);
        store.Confirm("daily-2026-09-10", 1, DateTimeOffset.UtcNow).Should().NotBeNull();
    }

    [Fact]
    public async Task T_10_1690_確定済みの方針は_REST_と同じ値を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        ConfirmDaily(factory, "押し目買いを優先する");
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        var restPolicy = await rest.GetFromJsonAsync<ConfirmedDailyPolicy>("/reports/daily-policy", ReportWire);
        var grpc = await new Proto.DailyPolicyRead.DailyPolicyReadClient(channel)
            .GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest());

        restPolicy!.Summary.Should().Be("押し目買いを優先する", "空どうしの一致は何も証明しない");
        grpc.Policy.Should().Be(DailyPolicyWireMapping.ToProto(restPolicy));
        grpc.Policy.Date.Should().Be("2026-09-10");
    }

    // 未確定は REST の 404 と同じく「方針なし」。gRPC は NOT_FOUND ではなく `policy` の無い応答（失敗として数えない）。
    [Fact]
    public async Task T_10_1690_未確定は_REST_の_404_と同じく方針の無い応答()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        (await rest.GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var grpc = await new Proto.DailyPolicyRead.DailyPolicyReadClient(channel)
            .GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest());

        grpc.Policy.Should().BeNull();
    }

    [Fact]
    public async Task T_10_1690_資格情報が無ければ_UNAUTHENTICATED_ロール不足は_PERMISSION_DENIED()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var anonymous = ChannelFor(factory, roles: null);
        using var unrelated = ChannelFor(factory, "some-unrelated-role");

        var anon = async () => await new Proto.DailyPolicyRead.DailyPolicyReadClient(anonymous)
            .GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest());
        var denied = async () => await new Proto.DailyPolicyRead.DailyPolicyReadClient(unrelated)
            .GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest());

        (await anon.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await denied.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        (await factory.CreateClient().GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "REST も同じ");
        using var restDenied = Rest(factory, "some-unrelated-role");
        (await restDenied.GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "REST も同じ");
    }

    // 陽性対照: 利用者（trading-owner）も読める（REST の read サブグループと同じ OwnerOrService）。
    [Fact]
    public async Task T_10_1690_利用者のロールでも読める()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        ConfirmDaily(factory, "様子見");
        using var channel = ChannelFor(factory, "trading-owner");

        (await new Proto.DailyPolicyRead.DailyPolicyReadClient(channel)
            .GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest())).Policy.Summary.Should().Be("様子見");
    }
}
