using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReportService.Tests.Features.Reports;

// NFR-06, FR-14, IADR-0626 決定 1・4: RiskManagement:BaseUrl があれば、報告書の確定の第 2 要素は状態の持ち主（リスク管理）へ
// 問い合わせて確かめる（失敗の数・停止・使ったコードを 2 サービスで共有する）。問い合わせは操作と対象（期間キーと版）を運ぶ。
// 段（Enforce）は報告書の構成のまま。届かなければ確かめられないものとして拒む（閉じる向き）。
public class ReportConfirmSharedSecondFactorTests
{
    [Fact]
    public async Task 確かめは持ち主へ問い合わせ_操作と対象とコードを運ぶ()
    {
        var owner = new OwnerStub("Verified");
        await using var factory = Factory(owner);
        var client = Owner(factory);
        var started = await StartAsync(client);

        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version, SecondFactorCode = "123456" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        owner.Requests.Should().ContainSingle().Which.Should().Be(
            new OwnerSecondFactorVerifyRequest("ReportConfirm", $"report:{started.PeriodKey}@v{started.Version}", "123456"));
    }

    [Theory]
    [InlineData("LockedOut")]
    [InlineData("Replayed")]
    [InlineData("Invalid")]
    public async Task 持ち主が拒めば確定しない(string outcome)
    {
        await using var factory = Factory(new OwnerStub(outcome));
        var client = Owner(factory);
        var started = await StartAsync(client);

        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version, SecondFactorCode = "123456" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 持ち主へ届かなければ確定しない()
    {
        await using var factory = Factory(new OwnerStub(null));
        var client = Owner(factory);
        var started = await StartAsync(client);

        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version, SecondFactorCode = "123456" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task コードが無ければ問い合わせない()
    {
        var owner = new OwnerStub("Verified");
        await using var factory = Factory(owner);
        var client = Owner(factory);
        var started = await StartAsync(client);

        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        owner.Requests.Should().BeEmpty();
    }

    private static WebApplicationFactory<Program> Factory(OwnerStub owner) =>
        new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting($"{OwnerSecondFactorOptions.SectionName}:Mode", "Enforce");
            b.UseSetting("RiskManagement:BaseUrl", "http://risk-management");
            b.ConfigureTestServices(s => s.AddHttpClient("risk-ledger").ConfigurePrimaryHttpMessageHandler(() => owner));
        });

    private static HttpClient Owner(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task<BootstrapStartDto> StartAsync(HttpClient client)
    {
        var created = await client.PostAsync("/reports/monthly-bootstrap", content: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<BootstrapStartDto>())!;
    }

    private sealed record BootstrapStartDto(string PeriodKey, int Version, bool Presented, bool NotificationFailed);

    // 持ち主（リスク管理）の代わり。確かめの口だけに答え、ほかの照会は 404（供給元は未供給に倒れる）。outcome が null なら届かない。
    private sealed class OwnerStub(string? outcome) : HttpMessageHandler
    {
        public ConcurrentQueue<OwnerSecondFactorVerifyRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != RemoteOwnerSecondFactorVerifier.Route)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            Requests.Enqueue((await request.Content!.ReadFromJsonAsync<OwnerSecondFactorVerifyRequest>(cancellationToken))!);
            if (outcome is null)
                throw new HttpRequestException("unreachable");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OwnerSecondFactorVerifyResponse(outcome)) };
        }
    }
}
