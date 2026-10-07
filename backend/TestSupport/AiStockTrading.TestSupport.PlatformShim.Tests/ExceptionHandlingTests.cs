using System.Net;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-06, NFR-05, IADR-0496, #1192: 未処理例外の応答は ProblemDetails で、要求ヘッダー（Authorization）・例外メッセージ・
// 型名・スタックを返さない。Development（開発者向けページが自動で挿入される環境）でも同じであることを実パイプラインで固定する。
public class ExceptionHandlingTests
{
    // 例外メッセージに混ぜる目印。応答に出たら漏れである。
    private const string SecretInMessage = "internal-detail-marker";

    // 実行時に組み立てる非秘密の目印（トークンに見える文字列をソースへ置かない）。
    private static string AuthorizationMarker() => "Bearer " + "placeholder-" + Guid.NewGuid().ToString("N");

    private static async Task<WebApplication> StartAsync(string environment, Action<WebApplication> map, bool install = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        if (install)
        {
            app.UseAiStockTradingExceptionHandler();
        }

        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static void MapThrowing(WebApplication app) =>
        app.MapGet("/boom", string () => throw new InvalidOperationException(SecretInMessage));

    // T-10-2350: Development／Production とも 500 の ProblemDetails で、Authorization・例外メッセージ・型名・スタックを含まない。
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task 未処理例外は環境に依らずヘッダーとスタックを含まない_ProblemDetails_になる(string environment)
    {
        await using var app = await StartAsync(environment, MapThrowing);
        var marker = AuthorizationMarker();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.TryAddWithoutValidation("Authorization", marker);

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("\"status\":500").And.Contain("traceId");
        body.Should().NotContain(marker["Bearer ".Length..]);
        body.Should().NotContain("Authorization");
        body.Should().NotContain(SecretInMessage);
        body.Should().NotContain(nameof(InvalidOperationException));
        body.Should().NotContain("   at ");
    }

    // 対照（試験の空振りを防ぐ）: 委譲を入れなければ、Development は要求ヘッダーごと開発者向けページを返す。
    [Fact]
    public async Task 対照_委譲が無い_Development_は開発者向けページで_Authorization_を返す()
    {
        await using var app = await StartAsync("Development", MapThrowing, install: false);
        var marker = AuthorizationMarker();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/boom");
        request.Headers.TryAddWithoutValidation("Authorization", marker);
        request.Headers.TryAddWithoutValidation("Accept", "text/plain");

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().Contain(marker["Bearer ".Length..]);
    }

    // T-10-2351: Development の最小 API は束縛失敗を BadHttpRequestException で投げる。状態（400）を保った ProblemDetails にする。
    [Fact]
    public async Task 束縛失敗の例外は_400_の_ProblemDetails_になる()
    {
        await using var app = await StartAsync("Development", a =>
            a.MapPost("/bind", (Payload p) => Results.Ok(p)));
        var marker = AuthorizationMarker();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bind")
        {
            Content = new StringContent("{\"value\":\"not-a-number\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", marker);

        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("\"status\":400");
        body.Should().NotContain(marker["Bearer ".Length..]);
        body.Should().NotContain("   at ");
    }

    // T-10-2352: 全サービス共通の終端は、例外処理の導入が無ければ起動を止める（付け忘れを黙って稼働させない）。
    [Fact]
    public async Task 終端は例外処理の導入が無ければ止める()
    {
        await using var bare = WebApplication.CreateBuilder().Build();
        var act = () => JasperFxCommandLine.EnsureExceptionHandlerInstalled(bare);
        act.Should().Throw<InvalidOperationException>().WithMessage("*IADR-0496*");

        // 終端そのもの（各サービスの Program.cs が通る経路）でも止まる。表明が外れていれば起動して直ちに止まる（＝例外にならない）。
        var builder0 = WebApplication.CreateBuilder();
        builder0.WebHost.UseTestServer();
        await using var bareHost = builder0.Build();
        bareHost.Lifetime.ApplicationStarted.Register(() => bareHost.Lifetime.StopApplication());
        var run = () => bareHost.RunAiStockTradingAsync(["--environment=Testing"]);
        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IADR-0496*");

        // 共通ミドルウェア（8 サービスが呼ぶ）経由でも導入される。
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        await using var installed = builder.Build();
        installed.UseAiStockTradingMiddleware();
        var ok = () => JasperFxCommandLine.EnsureExceptionHandlerInstalled(installed);
        ok.Should().NotThrow();
    }

    private sealed record Payload(int Value);
}
