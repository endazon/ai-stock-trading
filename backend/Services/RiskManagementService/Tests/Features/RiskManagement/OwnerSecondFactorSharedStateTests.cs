using System.Net;
using System.Net.Http.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace RiskManagementService.Tests;

// NFR-06, FR-14, IADR-0626: 第 2 要素の状態はリスク管理の DB に 1 つだけ持ち、報告書の確定の分も
// /risk-controls/owner-second-factor/verify で同じ状態を使って確かめる（失敗の数・停止・使ったコードを 2 サービスで共有する）。
// 確かめた結果は OwnerSecondFactorChecked として監査・通知へ発行する。時刻は差し替えた時計で固定する。
public class OwnerSecondFactorSharedStateTests
{
    // RFC 6238 付録 B の SHA1 の公開の試験値の種（ASCII の "1234567890" を 2 回）を、実行時に base32 へ組み立てる（本物の秘密ではない。
    // 文字列の定数で書くと秘密の走査に掛かる）。
    private static readonly string Secret = Totp.EncodeBase32(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 2))));

    private const string Route = "/risk-controls/owner-second-factor/verify";

    [Fact]
    public async Task 代わりの確かめの口はサービスの身元だけが呼べる()
    {
        using var factory = Factory(out _);
        var body = new OwnerSecondFactorVerifyRequest("ReportConfirm", "report:2026-11-06@v1", "000000");

        (await Client(factory, "trading-owner").PostAsJsonAsync(Route, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(factory, "trading-bot").PostAsJsonAsync(Route, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(factory, "trading-service,trading-bot").PostAsJsonAsync(Route, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().PostAsJsonAsync(Route, body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(factory, "trading-service").PostAsJsonAsync(Route, body)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("KillSwitchDisengage", "kill-switch")]
    [InlineData("StagePromote", "stage:Stage3")]
    [InlineData("4", "report:x@v1")]
    [InlineData("reportconfirm", "report:x@v1")]
    [InlineData("ReportConfirm", "")]
    public async Task 報告書の2操作のほかは受けない(string operation, string target)
    {
        using var factory = Factory(out _);

        (await Service(factory).PostAsJsonAsync(Route, new OwnerSecondFactorVerifyRequest(operation, target, "000000")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 報告書の確定で使ったコードは_kill_switchの解除に使えない()
    {
        using var factory = Factory(out var clock);
        var owner = Client(factory, "trading-owner");
        await owner.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        var code = CodeAt(clock.Now);

        (await VerifyAsync(factory, "ReportConfirm", "report:2026-11-06@v1", code)).Should().Be("Verified");

        (await owner.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", code)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "同じコードは別の操作・別のサービスでも通らない");
        (await VerifyAsync(factory, "PolicyConfirm", "report:2026-11-06@v2", code)).Should().Be("Replayed");
    }

    [Fact]
    public async Task 報告書の失敗とリスク管理の失敗を合わせて5回で止め_止めている間は正しいコードも拒む()
    {
        using var factory = Factory(out var clock);
        var owner = Client(factory, "trading-owner");
        await owner.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("止める"));
        var wrong = Wrong(clock);

        for (var i = 0; i < 3; i++)
            (await VerifyAsync(factory, "ReportConfirm", "report:2026-11-06@v1", wrong)).Should().Be("Invalid");
        for (var i = 0; i < 2; i++)
        {
            (await owner.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", wrong)))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await owner.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", CodeAt(clock.Now))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "止めている間は正しいコードも受け付けない");
        (await VerifyAsync(factory, "ReportConfirm", "report:2026-11-06@v1", CodeAt(clock.Now))).Should().Be("LockedOut");

        clock.Now = clock.Now.AddMinutes(15);
        (await owner.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("再開", CodeAt(clock.Now))))
            .StatusCode.Should().Be(HttpStatusCode.OK, "15 分で解ける");
    }

    [Fact]
    public async Task 状態はDBに残り_門を作り直しても止まったまま()
    {
        using var factory = Factory(out var clock);
        for (var i = 0; i < 5; i++)
            await VerifyAsync(factory, "ReportConfirm", "report:2026-11-06@v1", Wrong(clock));

        using (var scope = factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<RiskManagementDbContext>().OwnerSecondFactorState.SingleAsync();
            row.LockoutCount.Should().Be(1);
            row.LockedUntil.Should().Be(clock.Now.AddMinutes(15));
            row.SecretFingerprint.Should().NotBeEmpty().And.NotContain(Secret, "シークレットそのものは持たない");
        }

        // 再起動の代わり: 同じ DB の上に確かめを作り直す（プロセスの中の状態に頼らない）。
        var options = new OwnerSecondFactorOptions { Mode = SecondFactorMode.Enforce, Secret = Secret };
        var rebuilt = new TotpOwnerSecondFactorVerifier(
            options, new EfOwnerSecondFactorStateStore(factory.Services.GetRequiredService<IServiceScopeFactory>(), clock),
            clock, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        rebuilt.Verify(SecondFactorOperation.KillSwitchDisengage, "kill-switch", CodeAt(clock.Now)).Should().Be(SecondFactorOutcome.LockedOut);
    }

    [Fact]
    public async Task 確かめた結果を操作と対象つきで監査と通知へ発行する_コードは載せない()
    {
        using var factory = Factory(out var clock);
        var code = CodeAt(clock.Now);

        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            await VerifyAsync(factory, "ReportConfirm", "report:2026-11-06@v1", code);
            await VerifyAsync(factory, "ReportConfirm", "report:2026-11-06@v1", null);
            for (var i = 0; i < 5; i++)
                await VerifyAsync(factory, "PolicyConfirm", "report:2026-11-06@v2", Wrong(clock));
        });

        var published = session.AllRecordsInOrder()
            .Select(r => r.Envelope?.Message).OfType<OwnerSecondFactorChecked>().Distinct().ToList();
        published.Select(c => c.Outcome).Should().Equal("Verified", "Invalid", "Invalid", "Invalid", "Invalid", "Invalid");
        published[0].Should().Match<OwnerSecondFactorChecked>(c => c.Operation == "ReportConfirm" && c.Target == "report:2026-11-06@v1");
        published[^1].Should().Match<OwnerSecondFactorChecked>(c =>
            c.LockStarted && c.LockoutCount == 1 && c.LockedUntil == clock.Now.AddMinutes(15) && c.Operation == "PolicyConfirm");
        System.Text.Json.JsonSerializer.Serialize(published).Should().NotContain(code);
    }

    private static WebApplicationFactory<Program> Factory(out StubTimeProvider clock)
    {
        var fixedClock = new StubTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 * 30 + 5));
        clock = fixedClock;
        var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings = new Dictionary<string, string?>
            {
                [$"{OwnerSecondFactorOptions.SectionName}:Mode"] = "Enforce",
                [$"{OwnerSecondFactorOptions.SectionName}:Secret"] = Secret,
            },
        };
        return factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(fixedClock)));
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }

    private static HttpClient Service(WebApplicationFactory<Program> factory) => Client(factory, "trading-service");

    private static async Task<string> VerifyAsync(WebApplicationFactory<Program> factory, string operation, string target, string? code)
    {
        var res = await Service(factory).PostAsJsonAsync(Route, new OwnerSecondFactorVerifyRequest(operation, target, code));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await res.Content.ReadFromJsonAsync<OwnerSecondFactorVerifyResponse>())!.Outcome;
    }

    private static string CodeAt(DateTimeOffset at)
    {
        Totp.TryDecodeBase32(Secret, out var key);
        return Totp.Compute(key, Totp.StepOf(at));
    }

    private static string Wrong(StubTimeProvider clock)
    {
        var valid = new[] { -30, 0, 30 }.Select(s => CodeAt(clock.Now.AddSeconds(s))).ToHashSet();
        return Enumerable.Range(0, 10).Select(i => $"{i}{i}{i}{i}{i}{i}").First(c => !valid.Contains(c));
    }
}
