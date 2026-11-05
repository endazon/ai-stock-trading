using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AwesomeAssertions;
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
