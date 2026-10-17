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

// T-10-2760（提供側）, NFR, FR-07, FR-15, MSP:ADR-0029, IADR-0528 決定 3, IADR-0557 決定 3, #1388 (#753):
// 当時の確定日報の方針の **gRPC 面**（`DailyPolicyRead/GetConfirmedDailyPolicyAsOf`）。REST（`GET /reports/daily-policy/as-of`）と
// **同じ値・同じ入力の検証・同じ認可**であることを本物の Program.cs（ReportWorkerWebApplicationFactory）で固定する。
public class DailyPolicyAsOfGrpcTests
{
    private const string ServiceRole = "trading-service";
    private static readonly DateTimeOffset ConfirmedAt = new(2026, 9, 10, 21, 0, 0, TimeSpan.FromHours(9));
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
        store.Confirm("daily-2026-09-10", 1, ConfirmedAt).Should().NotBeNull();
    }

    private static Task<Proto.GetConfirmedDailyPolicyAsOfResponse> AsOfAsync(GrpcChannel channel, string at) =>
        new Proto.DailyPolicyRead.DailyPolicyReadClient(channel)
            .GetConfirmedDailyPolicyAsOfAsync(new Proto.GetConfirmedDailyPolicyAsOfRequest { At = at }).ResponseAsync;

    [Fact]
    public async Task T_10_2760_当時確定していた方針は_REST_と同じ値と確定の時刻を返す()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        ConfirmDaily(factory, "押し目買いを優先する");
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);
        const string at = "2026-09-11T00:00:00Z";

        var restPolicy = await rest.GetFromJsonAsync<ConfirmedDailyPolicyAsOf>($"/reports/daily-policy/as-of?at={at}", ReportWire);
        var grpc = await AsOfAsync(channel, at);

        restPolicy!.Summary.Should().Be("押し目買いを優先する", "空どうしの一致は何も証明しない");
        grpc.Policy.Should().Be(DailyPolicyWireMapping.ToProto(restPolicy));
        grpc.Policy.PeriodKey.Should().Be("daily-2026-09-10");
        grpc.HasConfirmedAt.Should().BeTrue();
        DateTimeOffset.Parse(grpc.ConfirmedAt, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(restPolicy.ConfirmedAt).And.Be(ConfirmedAt);
    }

    // 「無かった」は REST の 404 と同じく `policy` の無い応答（NOT_FOUND にしない）。確定より前の時点も「無かった」。
    [Theory]
    [InlineData(false, "2026-09-11T00:00:00Z")]
    [InlineData(true, "2026-09-10T11:59:59Z")] // 確定（12:00Z）の 1 秒前
    public async Task T_10_2760_無かったときは_REST_の_404_と同じく方針の無い応答(bool confirm, string at)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        if (confirm)
            ConfirmDaily(factory, "様子見");
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        (await rest.GetAsync($"/reports/daily-policy/as-of?at={at}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var grpc = await AsOfAsync(channel, at);

        grpc.Policy.Should().BeNull();
        grpc.HasConfirmedAt.Should().BeFalse();
    }

    // 🔴 入力の検証は REST と同じ 1 つ（オフセット必須）。REST の 400 は INVALID_ARGUMENT。
    [Theory]
    [InlineData("")]
    [InlineData("2026-09-11T00:00:00")]  // オフセットなし（現地時刻と読み違える）
    [InlineData("2026/09/11")]
    public async Task T_10_2760_時刻の欠落_オフセットの欠落_書式違いは_INVALID_ARGUMENT(string at)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var rest = Rest(factory);
        using var channel = ChannelFor(factory, ServiceRole);

        (await rest.GetAsync($"/reports/daily-policy/as-of?at={Uri.EscapeDataString(at)}")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "REST と同じ");
        var act = () => AsOfAsync(channel, at);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task T_10_2760_資格情報が無ければ_UNAUTHENTICATED_ロール不足は_PERMISSION_DENIED()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var anonymous = ChannelFor(factory, roles: null);
        using var unrelated = ChannelFor(factory, "some-unrelated-role");

        var anon = () => AsOfAsync(anonymous, "2026-09-11T00:00:00Z");
        var denied = () => AsOfAsync(unrelated, "2026-09-11T00:00:00Z");

        (await anon.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        (await denied.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }
}
