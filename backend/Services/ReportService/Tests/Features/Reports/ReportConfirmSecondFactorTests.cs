using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReportService.Features.Reports;
using ReportService.Features.Reports.ConfirmReport;
using Proto = AiStockTrading.Shared.Grpc.Report.V1;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ReportService.Tests.Features.Reports;

// NFR-06, FR-14, #1542, IADR-0615: 報告書の確定（Discord の /report approve・/policy の確定）は危険な操作で、第 2 要素（TOTP）を提供側で確かめる。
// 段 1（Audit）は記録だけで確定を止めず、段 2（Enforce）は確かめられなければ 403 で確定しない。既定（Off）は従来どおり。
public class ReportConfirmSecondFactorTests
{
    // RFC 6238 付録 B の SHA1 の公開の試験値の種（ASCII の "1234567890" を 2 回）を、実行時に base32 へ組み立てる（本物の秘密ではない。
    // 文字列の定数で書くと秘密の走査に掛かる）。
    private static readonly string Secret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 2))));

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData("Off", HttpStatusCode.OK)]
    [InlineData("Audit", HttpStatusCode.OK)]
    [InlineData("Enforce", HttpStatusCode.Forbidden)]
    public async Task コードの無い確定は段ごとに扱いが変わる(string? mode, HttpStatusCode expected)
    {
        await using var factory = Factory(mode);
        var client = Owner(factory);
        var started = await StartAsync(client);

        var res = await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version });

        res.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Enforceでも正しいコードがあれば確定する()
    {
        await using var factory = Factory("Enforce");
        var client = Owner(factory);
        var started = await StartAsync(client);
        Totp.TryDecodeBase32(Secret, out var key);
        var code = Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow));

        var res = await client.PostAsJsonAsync(
            $"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version, SecondFactorCode = code });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 IADR-0615 決定 9: 確定済みの冪等な再送・存在しない対象はコードを消費せず、Enforce でも従来どおりの応答。
    [Fact]
    public async Task Enforceでも確定済みの再送はコードなしで従来どおり200()
    {
        await using var factory = Factory("Enforce");
        var client = Owner(factory);
        var started = await StartAsync(client);
        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version, SecondFactorCode = CurrentCode() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resend = await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version });

        resend.StatusCode.Should().Be(HttpStatusCode.OK, "再送は冪等（Transitioned=false）で、門を通らない");
    }

    [Fact]
    public async Task Enforceでも存在しない対象は404でコードを消費しない()
    {
        await using var factory = Factory("Enforce");
        var client = Owner(factory);
        var code = CurrentCode();

        (await client.PostAsJsonAsync("/reports/daily-2000-01-01/confirm", new { ExpectedVersion = 1, SecondFactorCode = code }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var started = await StartAsync(client);
        (await client.PostAsJsonAsync($"/reports/{started.PeriodKey}/confirm", new { ExpectedVersion = started.Version, SecondFactorCode = code }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "404 の要求でコードが消費されていない");
    }

    [Fact]
    public void Offなら門のcallbackを作らず方針の台帳も読まない()
    {
        var ledger = new ThrowingLedger();
        var http = Http(SecondFactorMode.Off);

        ConfirmReportEndpoint.SecondFactorBeforeCommit(http, ledger, "daily-2026-11-05", new ConfirmReportRequest(1), NullLoggerFactory.Instance)
            .Should().BeNull();
        ledger.Calls.Should().Be(0);
    }

    [Fact]
    public void Auditで方針の台帳が読めなくても確定を壊さない()
    {
        var ledger = new ThrowingLedger();
        var callback = ConfirmReportEndpoint.SecondFactorBeforeCommit(
            Http(SecondFactorMode.Audit), ledger, "daily-2026-11-05", new ConfirmReportRequest(1), NullLoggerFactory.Instance);

        callback.Should().NotBeNull();
        callback!.Invoking(c => c()).Should().NotThrow();
        ledger.Calls.Should().Be(1);
    }

    // NFR-06, #1542, IADR-0615 決定 4: gRPC の面（ボットの段 5 の経路）でも Enforce の拒否は PERMISSION_DENIED。
    [Fact]
    public async Task EnforceのgRPCの確定はコードが無ければPERMISSION_DENIED()
    {
        await using var factory = Factory("Enforce");
        var started = await StartAsync(Owner(factory));
        var grpc = new Proto.ReportOwnerWrite.ReportOwnerWriteClient(GrpcChannel.ForAddress(factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = new BotHeaders(factory.Server.CreateHandler()) }));

        var act = async () => await grpc.ConfirmReportAsync(new Proto.ReportConfirmationRequest { PeriodKey = started.PeriodKey, ExpectedVersion = started.Version });

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    private static string CurrentCode()
    {
        Totp.TryDecodeBase32(Secret, out var key);
        return Totp.Compute(key, Totp.StepOf(DateTimeOffset.UtcNow));
    }

    private static DefaultHttpContext Http(SecondFactorMode mode) => new()
    {
        RequestServices = new ServiceCollection()
            .AddSingleton(new OwnerSecondFactorGate(new OwnerSecondFactorOptions { Mode = mode, Secret = Secret }, TimeProvider.System, NullLogger.Instance))
            .BuildServiceProvider(),
    };

    private sealed class ThrowingLedger : IPolicyRevisionLedger
    {
        public int Calls { get; private set; }

        public PolicyRevisionAttempt? FindProposed(string periodKey, int reportVersion)
        {
            Calls++;
            throw new InvalidOperationException("台帳を読めない");
        }

        public int CountOn(DateOnly jstDate) => throw new NotSupportedException();
        public Guid Begin(PolicyRevisionAttempt attempt) => throw new NotSupportedException();
        public PolicyRevisionBeginResult TryBegin(PolicyRevisionAttempt attempt, int dailyLimit) => throw new NotSupportedException();
        public void Complete(Guid id, PolicyRevisionAttemptOutcome outcome, int? reportVersion, string? watchlistChangesJson) => throw new NotSupportedException();
        public PolicyRevisionAttempt? Find(Guid id) => throw new NotSupportedException();
        public bool RecordWatchlistApply(Guid id, string resultJson, DateTimeOffset recordedAt) => throw new NotSupportedException();
        public void MarkProposalConfirmed(Guid id, DateTimeOffset confirmedAt) => throw new NotSupportedException();
    }

    private sealed class BotHeaders(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(TestAuthHandler.RolesHeader, "trading-owner");
            request.Headers.TryAddWithoutValidation(TestAuthHandler.NameHeader, "report-bot");
            request.Headers.TryAddWithoutValidation(TestAuthHandler.AzpHeader, "ai-stock-trading-owner");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static WebApplicationFactory<Program> Factory(string? mode) =>
        new ReportWorkerWebApplicationFactory().WithWebHostBuilder(b =>
        {
            if (mode is not null)
                b.UseSetting($"{OwnerSecondFactorOptions.SectionName}:Mode", mode);
            b.UseSetting($"{OwnerSecondFactorOptions.SectionName}:Secret", Secret);
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
}
