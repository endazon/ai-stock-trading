using System.Net;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InformationCollectionService.Tests;

// NFR-06, NFR-05, IADR-0496（#1205 監査の追記）, #1192: 情報収集は共通ミドルウェアを使わず、例外処理と認証・認可を Program.cs で
// 個別に並べている。認証を明示しないと WebApplication が例外処理の**外側**へ自動で挿入し、Development（docker-compose の既定）で
// 認証スキームが投げると（Keycloak 不達時の JwtBearer の鍵取得失敗など）開発者向けページが Authorization ヘッダーごと応答する。
// 実ホスト（Program.cs そのもの）を Development で起動し、投げる認証スキームでも ProblemDetails になることを固定する。
public class AuthenticationExceptionProblemDetailsTests
{
    private const string SecretInMessage = "ic-auth-internal-detail-marker";

    // T-10-2363: Development ＋ 投げる認証スキーム ＋ Bearer 付きの要求 → 500 の ProblemDetails（Authorization・例外メッセージ・スタックなし）。
    [Fact]
    public async Task Development_で認証スキームが投げても_Authorization_を返さず_ProblemDetails_になる()
    {
        await using var baseFactory = new InformationCollectionWorkerWebApplicationFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
                services.AddAuthentication(ThrowingAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, ThrowingAuthHandler>(ThrowingAuthHandler.SchemeName, _ => { }));
        });
        var marker = "Bearer " + "placeholder-" + Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/collection/run-once");
        request.Headers.TryAddWithoutValidation("Authorization", marker);
        request.Headers.TryAddWithoutValidation("Accept", "text/plain");

        var response = await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        body.Should().Contain("\"status\":500").And.Contain("traceId");
        body.Should().NotContain(marker["Bearer ".Length..]);
        body.Should().NotContain("Authorization");
        body.Should().NotContain(SecretInMessage);
        body.Should().NotContain("   at ");
    }

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
