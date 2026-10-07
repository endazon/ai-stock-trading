using System.Net;
using System.Text.Encodings.Web;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-06, NFR-05, IADR-0496（#1205 監査の追記）, #1192: 認証スキームの例外も例外処理の内側で ProblemDetails になること。
// WebApplication は認証・認可を**明示しなければ**利用者のパイプラインより外側に自動で挿入する。その場合、JwtBearer の例外
// （Keycloak 不達時の鍵取得失敗など）は例外処理を素通りし、Development では開発者向けページが Authorization ヘッダーごと応答する。
// 本クラスは ① その危険が実在すること（対照）、② 明示の挿入（情報収集の形）と共通ミドルウェア（8 サービスの形）がそれを塞ぐこと、
// ③ 付け忘れ・順序違い・DI 検証の付け忘れを共通の終端が起動時に止めることを固定する。
public class ExceptionHandlingAuthenticationTests
{
    private const string SecretInMessage = "auth-internal-detail-marker";

    private static string AuthorizationMarker() => "Bearer " + "placeholder-" + Guid.NewGuid().ToString("N");

    private static WebApplicationBuilder ThrowingAuthBuilder(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(ThrowingAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ThrowingAuthHandler>(ThrowingAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        return builder;
    }

    private static async Task<(HttpResponseMessage Response, string Body, string Marker)> SendBearerAsync(WebApplication app)
    {
        app.MapGet("/secured", () => "ok").RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var marker = AuthorizationMarker();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/secured");
        request.Headers.TryAddWithoutValidation("Authorization", marker);
        request.Headers.TryAddWithoutValidation("Accept", "text/plain");
        var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, body, marker);
    }

    private static void ShouldBeSafeProblem(HttpResponseMessage response, string body, string marker)
    {
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("\"status\":500").And.Contain("traceId");
        body.Should().NotContain(marker["Bearer ".Length..]);
        body.Should().NotContain("Authorization");
        body.Should().NotContain(SecretInMessage);
        body.Should().NotContain("   at ");
    }

    // 対照（試験の空振りを防ぐ）: 例外処理を入れても認証を明示しなければ、Development の認証例外は開発者向けページで Authorization を返す。
    [Fact]
    public async Task 対照_認証を明示しないと_認証の例外は例外処理の外へ抜けて_Authorization_を返す()
    {
        await using var app = ThrowingAuthBuilder("Development").Build();
        app.UseAiStockTradingExceptionHandler();

        var (_, body, marker) = await SendBearerAsync(app);

        body.Should().Contain(marker["Bearer ".Length..]);
    }

    // T-10-2358: 例外処理の直後に UseAuthentication / UseAuthorization を明示する形（情報収集の Program.cs）では、
    // Development でも認証スキームの例外が ProblemDetails（Authorization・例外メッセージ・スタックなし）になる。
    [Fact]
    public async Task 認証を例外処理の直後に明示すれば_Development_の認証例外も_ProblemDetails_になる()
    {
        await using var app = ThrowingAuthBuilder("Development").Build();
        app.UseAiStockTradingExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();

        var (response, body, marker) = await SendBearerAsync(app);

        ShouldBeSafeProblem(response, body, marker);
    }

    // T-10-2359: 共通ミドルウェア（8 サービスの Program.cs が呼ぶ UseAiStockTradingMiddleware）でも同じ。
    // 例外処理を UseAuthorization の後ろへ動かす変異で赤になる（順序の表明が呼び出し時に止める）。
    [Fact]
    public async Task 共通ミドルウェア経由でも_Development_の認証例外は_ProblemDetails_になる()
    {
        await using var app = ThrowingAuthBuilder("Development").Build();
        app.UseAiStockTradingMiddleware();

        var (response, body, marker) = await SendBearerAsync(app);

        ShouldBeSafeProblem(response, body, marker);
    }

    // T-10-2360: 表明が拠り所にする印のキー名を実測で固定する（ASP.NET Core の内部名。変われば本試験が赤になる）。
    [Fact]
    public async Task UseAuthentication_と_UseAuthorization_はそれぞれの印を置く()
    {
        await using var app = ThrowingAuthBuilder("Production").Build();
        var properties = ((IApplicationBuilder)app).Properties;
        properties.Should().NotContainKey(ExceptionHandlingExtensions.AuthenticationMiddlewareSetKey);
        properties.Should().NotContainKey(ExceptionHandlingExtensions.AuthorizationMiddlewareSetKey);

        app.UseAuthentication();
        app.UseAuthorization();

        properties.Should().ContainKey(ExceptionHandlingExtensions.AuthenticationMiddlewareSetKey);
        properties.Should().ContainKey(ExceptionHandlingExtensions.AuthorizationMiddlewareSetKey);
    }

    // T-10-2361: 認証を登録して明示しないサービス・例外処理より前に認証を入れたサービスは起動させない。
    [Fact]
    public async Task 認証の明示漏れと順序違いは起動前に止まる()
    {
        // 明示漏れ: 共通の終端そのもの（各サービスの Program.cs が通る経路）で止まる。
        var builder = ThrowingAuthBuilder("Production");
        builder.UseAiStockTradingServiceProviderValidation();
        await using var missing = builder.Build();
        missing.UseAiStockTradingExceptionHandler();
        missing.Lifetime.ApplicationStarted.Register(() => missing.Lifetime.StopApplication());
        var run = () => missing.RunAiStockTradingAsync(["--environment=Testing"]);
        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseAuthentication*IADR-0496*");

        // 認可だけの明示漏れも止まる（自動挿入の認可も外側に入り、ポリシーの評価で認証スキームを呼ぶ）。
        await using var authzMissing = ThrowingAuthBuilder("Production").Build();
        authzMissing.UseAiStockTradingExceptionHandler();
        authzMissing.UseAuthentication();
        var authz = () => JasperFxCommandLine.EnsureAuthenticationInsideExceptionHandler(authzMissing);
        authz.Should().Throw<InvalidOperationException>().WithMessage("*UseAuthorization*");

        // 順序違い: 認証を先に入れてから例外処理を呼ぶと、その場で止まる。
        await using var reversed = ThrowingAuthBuilder("Production").Build();
        reversed.UseAuthentication();
        reversed.UseAuthorization();
        var install = () => reversed.UseAiStockTradingExceptionHandler();
        install.Should().Throw<InvalidOperationException>().WithMessage("*より前*");

        // 陽性対照: 明示した形と、認証を登録しないサービス（通知・取引判断）の形は通る。
        await using var ok = ThrowingAuthBuilder("Production").Build();
        ok.UseAiStockTradingMiddleware();
        var okAct = () => JasperFxCommandLine.EnsureAuthenticationInsideExceptionHandler(ok);
        okAct.Should().NotThrow();

        await using var noAuth = WebApplication.CreateBuilder().Build();
        noAuth.UseAiStockTradingExceptionHandler();
        var noAuthAct = () => JasperFxCommandLine.EnsureAuthenticationInsideExceptionHandler(noAuth);
        noAuthAct.Should().NotThrow();
    }

    // T-10-2362: DI 検証（ValidateScopes・ValidateOnBuild）は Production でも有効で、付け忘れは共通の終端が止める。
    [Fact]
    public async Task DI_検証は_Production_でも有効で_付け忘れは起動前に止まる()
    {
        // 対照: 既定の Production は singleton が scoped を捕まえる誤配線をそのまま組み立てる（検証が外れている）。
        var plain = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        plain.Services.AddScoped<ScopedDependency>();
        plain.Services.AddSingleton<SingletonCapturingScoped>();
        var buildPlain = () => plain.Build();
        await using (var built = buildPlain.Should().NotThrow().Subject)
        {
            var guard = () => JasperFxCommandLine.EnsureServiceProviderValidation(built);
            guard.Should().Throw<InvalidOperationException>().WithMessage("*UseAiStockTradingServiceProviderValidation*");
        }

        // 有効にすると Production でも Build の時点で止まる。
        var validated = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        validated.UseAiStockTradingServiceProviderValidation();
        validated.Services.AddScoped<ScopedDependency>();
        validated.Services.AddSingleton<SingletonCapturingScoped>();
        var buildValidated = () => validated.Build();
        buildValidated.Should().Throw<AggregateException>().WithInnerException<InvalidOperationException>();

        // 共通の終端そのもの（各サービスの Program.cs が通る経路）でも、付け忘れは起動前に止まる。
        var missing = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        missing.WebHost.UseTestServer();
        await using var missingApp = missing.Build();
        missingApp.UseAiStockTradingExceptionHandler();
        missingApp.Lifetime.ApplicationStarted.Register(() => missingApp.Lifetime.StopApplication());
        var runMissing = () => missingApp.RunAiStockTradingAsync(["--environment=Testing"]);
        await runMissing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UseAiStockTradingServiceProviderValidation*");

        // 陽性対照: 誤配線が無く導入が揃っていれば、終端は起動して（直ちに止めて）正常に終わる。
        var clean = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        clean.WebHost.UseTestServer();
        clean.UseAiStockTradingServiceProviderValidation();
        await using var cleanApp = clean.Build();
        cleanApp.UseAiStockTradingExceptionHandler();
        cleanApp.Lifetime.ApplicationStarted.Register(() => cleanApp.Lifetime.StopApplication());
        (await cleanApp.RunAiStockTradingAsync(["--environment=Testing"])).Should().Be(0);
    }

    private sealed class ScopedDependency;

    private sealed class SingletonCapturingScoped(ScopedDependency dependency)
    {
        public ScopedDependency Dependency { get; } = dependency;
    }

    // Keycloak 不達時の JwtBearer（IDX20803 等）を模す: 認証のたびに投げる。
    private sealed class ThrowingAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Throwing";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            throw new InvalidOperationException(SecretInMessage);
    }
}
