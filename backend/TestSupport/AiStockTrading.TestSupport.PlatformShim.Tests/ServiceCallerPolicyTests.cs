using System.Net;
using System.Security.Cryptography;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, NFR-05, NFR-06, IADR-0637: サービスのトークンを身元（azp）と宛先（aud）で確かめ、口ごとに呼び手を限る。
// 本物の登録（AddAiStockTradingAuth）と本物の JwtBearer（鍵は試験ごとの乱数。発行元は固定の試験用の値）を TestServer で通す。
//   - Enforce: 宛先違いは 401、一覧に無い呼び手・名前の無い口は 403、サービスだけの口は一覧のクライアントだけ。
//   - 移行の段（LegacyClients）: 旧の共有クライアントは宛先を問わずに通る。段を外すと 401。
//   - Report・Off（既定）: 従前どおり通す（陰性対照: 同じトークンが Enforce では落ちる）。
public class ServiceCallerPolicyTests
{
    private const string Issuer = "https://keycloak.test/realms/platform";
    private const string Api = "ai-stock-trading-api";
    private const string Report = "ai-stock-trading-svc-report";
    private const string TradeDecision = "ai-stock-trading-svc-trade-decision";
    private const string Legacy = "ai-stock-trading-svc";
    private const string Owner = "ai-stock-trading-owner";

    private static readonly Dictionary<string, string?> Callers = new()
    {
        [$"{ServiceTokenBinding.CallersKey}:open-positions:0"] = Report,
        [$"{ServiceTokenBinding.CallersKey}:open-positions:1"] = TradeDecision,
        [$"{ServiceTokenBinding.CallersKey}:verify:0"] = Report,
    };

    private static Dictionary<string, string?> Settings(string? mode, bool legacy = false)
    {
        var d = new Dictionary<string, string?>(Callers);
        if (mode is not null)
            d[ServiceTokenBinding.ModeKey] = mode;
        if (legacy)
            d[$"{ServiceTokenBinding.LegacyClientsKey}:0"] = Legacy;
        return d;
    }

    private sealed class Host(WebApplication app, SymmetricSecurityKey key) : IAsyncDisposable
    {
        public async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string? token)
        {
            using var request = new HttpRequestMessage(method, path);
            if (token is not null)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);
            return response.StatusCode;
        }

        public string Token(string azp, string[] roles, params string[] audiences)
        {
            var claims = new Dictionary<string, object>
            {
                ["azp"] = azp,
                ["preferred_username"] = "service-account-" + azp,
                ["realm_access"] = new Dictionary<string, object> { ["roles"] = roles },
            };
            if (audiences.Length > 0)
                claims["aud"] = audiences;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Claims = claims,
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
        }

        public string Service(string azp, params string[] audiences) => Token(azp, ["trading-service"], audiences);

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Host> StartAsync(IDictionary<string, string?> settings)
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(settings) { ["Auth:Authority"] = Issuer })
            .Build();
        builder.Services.AddAiStockTradingAuth(config);
        // 鍵の取得（メタデータ）を試験の鍵へ置き換える（発行元・宛先・署名の検証は本物の JwtBearer が行う）。
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer };
            metadata.SigningKeys.Add(key);
            o.Configuration = metadata;
            o.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/open-positions", () => "ok")
            .RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService)
            .RequireServiceCallers("open-positions");
        app.MapGet("/unnamed", () => "ok").RequireAuthorization(AiStockTradingAuthPolicies.OwnerOrService);
        app.MapPost("/verify", () => "ok")
            .RequireAuthorization(p => p.RequireAuthenticatedUser().RequireRole("trading-service"))
            .RequireServiceCallers("verify");
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new Host(app, key);
    }

    [Fact]
    public async Task Enforce_一覧のクライアントで宛先が合えば通る()
    {
        await using var host = await StartAsync(Settings("Enforce"));
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Service(Report, Api))).Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Service(TradeDecision, Api, "platform-api"))).Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("ai-stock-trading-svc-market-monitor")]
    [InlineData("AI-STOCK-TRADING-SVC-REPORT")]
    [InlineData("ai-stock-trading-svc-report ")]
    [InlineData("ai-stock-trading-svc")]
    public async Task Enforce_一覧に無い呼び手は403(string azp)
    {
        await using var host = await StartAsync(Settings("Enforce"));
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Service(azp, Api))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Enforce_名前の無い口はサービスに開かない_所有者は従前どおり()
    {
        await using var host = await StartAsync(Settings("Enforce"));
        (await host.SendAsync(HttpMethod.Get, "/unnamed", host.Service(Report, Api))).Should().Be(HttpStatusCode.Forbidden);
        (await host.SendAsync(HttpMethod.Get, "/unnamed", host.Token(Owner, ["trading-owner"], Api))).Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Token(Owner, ["trading-owner"], Api))).Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("platform-api")]
    [InlineData("AI-STOCK-TRADING-API")]
    [InlineData("account,platform-api")]
    public async Task Enforce_宛先が違うトークンは401_サービスも所有者も(string audienceList)
    {
        var audiences = audienceList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        await using var host = await StartAsync(Settings("Enforce"));
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Service(Report, audiences))).Should().Be(HttpStatusCode.Unauthorized);
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Token(Owner, ["trading-owner"], audiences))).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Enforce_サービスだけの口は一覧のクライアントだけ()
    {
        await using var host = await StartAsync(Settings("Enforce"));
        (await host.SendAsync(HttpMethod.Post, "/verify", host.Service(Report, Api))).Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Post, "/verify", host.Service(TradeDecision, Api))).Should().Be(HttpStatusCode.Forbidden);
        (await host.SendAsync(HttpMethod.Post, "/verify", host.Service("ai-stock-trading-svc-order-execution", Api))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 移行の段_旧の共有クライアントは宛先を問わず通る_段を外すと401()
    {
        var legacyToken = (Host h) => h.Service(Legacy, "platform-api");
        await using (var migrating = await StartAsync(Settings("Enforce", legacy: true)))
        {
            (await migrating.SendAsync(HttpMethod.Get, "/open-positions", legacyToken(migrating))).Should().Be(HttpStatusCode.OK);
            (await migrating.SendAsync(HttpMethod.Get, "/unnamed", legacyToken(migrating))).Should().Be(HttpStatusCode.OK);
            (await migrating.SendAsync(HttpMethod.Post, "/verify", legacyToken(migrating))).Should().Be(HttpStatusCode.OK);
            // 移行の段でも、新しいクライアントは一覧で限られる（旧の扱いは旧のクライアントだけ）。
            (await migrating.SendAsync(HttpMethod.Post, "/verify", migrating.Service(TradeDecision, Api))).Should().Be(HttpStatusCode.Forbidden);
            (await migrating.SendAsync(HttpMethod.Get, "/open-positions", migrating.Service(TradeDecision, "platform-api"))).Should().Be(HttpStatusCode.Unauthorized);
        }

        await using var final = await StartAsync(Settings("Enforce"));
        (await final.SendAsync(HttpMethod.Get, "/open-positions", legacyToken(final))).Should().Be(HttpStatusCode.Unauthorized);
        (await final.SendAsync(HttpMethod.Get, "/open-positions", final.Service(Legacy, Api))).Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Off")]
    [InlineData("Report")]
    public async Task 既定_Off_Report_は従前どおり通す(string? mode)
    {
        await using var host = await StartAsync(Settings(mode));
        (await host.SendAsync(HttpMethod.Get, "/open-positions", host.Service("ai-stock-trading-svc-market-monitor", "platform-api"))).Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Get, "/unnamed", host.Service(Legacy))).Should().Be(HttpStatusCode.OK);
        (await host.SendAsync(HttpMethod.Post, "/verify", host.Service(TradeDecision))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 無認証は段に依らず401()
    {
        await using var host = await StartAsync(Settings("Enforce"));
        (await host.SendAsync(HttpMethod.Get, "/open-positions", null)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(ServiceTokenBinding.ModeKey, "enforce")]
    [InlineData(ServiceTokenBinding.ModeKey, "Strict")]
    [InlineData(ServiceTokenBinding.LegacyClientsKey, "ai-stock-trading-svc")]
    [InlineData(ServiceTokenBinding.AudiencesKey, "ai-stock-trading-api")]
    [InlineData(ServiceTokenBinding.CallersKey, "x")]
    [InlineData(ServiceTokenBinding.CallersKey + ":open-positions", "ai-stock-trading-svc-report")]
    public void 構成の誤りは起動で止まる(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var resolve = () => ServiceTokenBinding.Resolve(configuration);
        resolve.Should().Throw<InvalidOperationException>().WithMessage("*IADR-0637*");
    }

    [Fact]
    public void Enforce_で空の宛先は起動で止まる_既定は_Off_宛先は_ai_stock_trading_api()
    {
        var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ServiceTokenBinding.ModeKey] = "Enforce",
            [$"{ServiceTokenBinding.AudiencesKey}:0"] = " ",
        }).Build();
        var resolve = () => ServiceTokenBinding.Resolve(empty);
        resolve.Should().Throw<InvalidOperationException>();

        var defaults = ServiceTokenBinding.Resolve(new ConfigurationBuilder().Build());
        defaults.Mode.Should().Be(ServiceTokenMode.Off);
        defaults.Audiences.Should().Equal(Api);
        defaults.LegacyClients.Should().BeEmpty();
    }
}
