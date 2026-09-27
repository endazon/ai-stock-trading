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

    private static GrpcChannel ChannelFor(WebApplicationFactory<Program> factory, string? roles, string? azp = null)
    {
        var handler = factory.Server.CreateHandler();
        if (roles is not null)
            handler = new RolesHeaderHandler(handler, roles, azp);
        return GrpcChannel.ForAddress(factory.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = handler });
    }

    private sealed class RolesHeaderHandler(HttpMessageHandler inner, string roles, string? azp = null) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, roles);
            if (azp is not null)
                request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, azp);
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
    // #1067: gRPC 面の所有者の分岐は、トークンの azp がボットの機密クライアントであるときだけ（T-10-1725）。
    [Fact]
    public async Task T_10_1690_利用者のロールでも読める()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        ConfirmDaily(factory, "様子見");
        using var channel = ChannelFor(factory, "trading-owner", "ai-stock-trading-owner");

        (await new Proto.DailyPolicyRead.DailyPolicyReadClient(channel)
            .GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest())).Policy.Summary.Should().Be("様子見");
    }

    // ---- T-10-1725: gRPC 面の所有者の門は、呼び出し元のクライアント（azp）が Discord ボットであることを併せて求める ----
    // NFR-06, FR-14, ADR-0047 決定 3, IADR-0448 決定 1, #1067 (#753): 人の利用者のトークン（`trading-owner` を持つが azp は BFF 等）は
    // gRPC 面を通らない。🔴 変種（大小文字・接頭辞・接尾辞）と azp の無いトークンも通さない。s2s の分岐と REST の面は変えない。
    private const string GateOwnerRole = "trading-owner";
    private const string GateServiceRole = "trading-service";
    private const string BotClient = "ai-stock-trading-owner";

    [Theory]
    [InlineData(null)]                          // azp の無いトークン
    [InlineData("ai-stock-trading-dev")]        // 利用者の公開クライアント（ブラウザ・BFF の経路）
    [InlineData("bff")]
    [InlineData("AI-STOCK-TRADING-OWNER")]      // 大小文字の変種
    [InlineData("Ai-Stock-Trading-Owner")]
    [InlineData("ai-stock-trading-owner-bff")]  // ボットの id を接頭辞に持つ別のクライアント
    [InlineData("ai-stock-trading-own")]        // ボットの id の接頭辞
    [InlineData("xai-stock-trading-owner")]     // ボットの id を接尾辞に持つ別のクライアント
    [InlineData("ai-stock-trading-svc")]        // s2s のクライアントでも、所有者の分岐では通さない
    public async Task T_10_1725_所有者のロールでも呼び出し元がボットでなければ_PERMISSION_DENIED(string? azp)
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        using var channel = ChannelFor(factory, GateOwnerRole, azp);

        var act = async () => await new Proto.DailyPolicyRead.DailyPolicyReadClient(channel).GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    // 陽性対照: ボットのトークン（trading-owner ＋ azp＝ボットの機密クライアント）は通る。s2s は azp を問わず従来どおり。
    // 🔴 REST の面の所有者の判定は変えない（azp の無い利用者のトークンで REST は読める＝BFF が中継する経路）。
    [Fact]
    public async Task T_10_1725_ボットのトークンは通り_s2sは従来どおりで_RESTの所有者の判定は変えない()
    {
        await using var factory = new ReportWorkerWebApplicationFactory();
        ConfirmDaily(factory, "様子見");
        foreach (var (roles, azp) in new (string, string?)[]
                 {
                     (GateOwnerRole, BotClient),
                     (GateServiceRole, null),
                     (GateServiceRole, "bff"),
                     ($"{GateOwnerRole},{GateServiceRole}", "bff"),
                 })
        {
            using var channel = ChannelFor(factory, roles, azp);
            var act = async () => await new Proto.DailyPolicyRead.DailyPolicyReadClient(channel).GetConfirmedDailyPolicyAsync(new Proto.GetConfirmedDailyPolicyRequest());
            await act.Should().NotThrowAsync($"roles={roles} azp={azp ?? "(無し)"} は通るはず");
        }

        using var rest = Rest(factory, GateOwnerRole);
        (await rest.GetAsync("/reports/daily-policy")).StatusCode.Should().Be(HttpStatusCode.OK, "REST の所有者の判定は OwnerOrService のまま");
    }
}
