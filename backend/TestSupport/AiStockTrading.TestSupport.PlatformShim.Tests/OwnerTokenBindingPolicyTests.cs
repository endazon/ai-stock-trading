using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, #1528, IADR-0611: REST の所有者の判定（OwnerOnly・OwnerOrService の所有者の分岐）で aud と azp を確かめる。
// 本物の登録（AddAiStockTradingAuth）が作るポリシーを IAuthorizationService で評価する。
// 段 1（既定）は従前どおり通し、段 2（Enforce）は合わないトークンを通さない。サービスの分岐（trading-service）は変えない。
public class OwnerTokenBindingPolicyTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";

    private static ClaimsPrincipal Principal(string[] roles, string[] auds, params string[] azps)
    {
        var claims = new List<Claim> { new("preferred_username", "someone") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(auds.Select(a => new Claim(OwnerTokenBinding.AudienceClaim, a)));
        claims.AddRange(azps.Select(a => new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, a)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "preferred_username", ClaimTypes.Role));
    }

    private static readonly Dictionary<string, string?> Enforce = new() { [OwnerTokenBinding.ModeKey] = "Enforce" };

    private static async Task<bool> Evaluate(ClaimsPrincipal user, string policy, IDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        using var provider = services.BuildServiceProvider();
        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy)).Succeeded;
    }

    [Theory]
    [InlineData(AiStockTradingAuthPolicies.OwnerOnly, "bff")]
    [InlineData(AiStockTradingAuthPolicies.OwnerOnly, "ai-stock-trading-owner")]
    [InlineData(AiStockTradingAuthPolicies.OwnerOrService, "bff")]
    [InlineData(AiStockTradingAuthPolicies.OwnerOrService, "ai-stock-trading-owner")]
    public async Task 段2_既定の宛先とクライアントの所有者トークンは通る(string policy, string azp) =>
        (await Evaluate(Principal([OwnerRole], ["ai-stock-trading-api"], azp), policy, Enforce)).Should().BeTrue();

    // 否定の試験: 宛先が違う・無い、クライアントが違う・無い・複数・変種。
    public static TheoryData<string[], string[]> Mismatches => new()
    {
        { ["account"], ["bff"] },
        { [], ["bff"] },
        { ["ai-stock-trading-api"], ["account-console"] },
        { ["ai-stock-trading-api"], [] },
        { ["ai-stock-trading-api"], ["bff", "ai-stock-trading-owner"] },
        { ["ai-stock-trading-api"], ["BFF"] },
        { ["AI-STOCK-TRADING-API"], ["bff"] },
        // 🔴 MSP 共通の宛先は AST の宛先ではない（MSP の全サービスアカウントのトークンに付く）。
        { ["platform-api"], ["bff"] },
        { ["platform-api"], ["ai-stock-trading-owner"] },
    };

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task 段2_宛先かクライアントが合わない所有者トークンは拒否(string[] auds, string[] azps)
    {
        (await Evaluate(Principal([OwnerRole], auds, azps), AiStockTradingAuthPolicies.OwnerOnly, Enforce)).Should().BeFalse();
        (await Evaluate(Principal([OwnerRole], auds, azps), AiStockTradingAuthPolicies.OwnerOrService, Enforce)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task 段1_合わない所有者トークンも従前どおり通る(string[] auds, string[] azps)
    {
        (await Evaluate(Principal([OwnerRole], auds, azps), AiStockTradingAuthPolicies.OwnerOnly)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole], auds, azps), AiStockTradingAuthPolicies.OwnerOrService)).Should().BeTrue();
    }

    [Fact]
    public async Task サービスの分岐は段2でも_aud_azp_を見ない_所有者限定には開かない()
    {
        (await Evaluate(Principal([ServiceRole], []), AiStockTradingAuthPolicies.OwnerOrService, Enforce)).Should().BeTrue();
        (await Evaluate(Principal([ServiceRole], ["ai-stock-trading-api"], "ai-stock-trading-svc"), AiStockTradingAuthPolicies.OwnerOnly, Enforce))
            .Should().BeFalse("s2s は OwnerOnly を通らない（従前どおり）");
    }

    [Fact]
    public async Task ロールが無ければ段に依らず拒否()
    {
        (await Evaluate(Principal(["unrelated"], ["ai-stock-trading-api"], "bff"), AiStockTradingAuthPolicies.OwnerOnly)).Should().BeFalse();
        (await Evaluate(Principal(["unrelated"], ["ai-stock-trading-api"], "bff"), AiStockTradingAuthPolicies.OwnerOnly, Enforce)).Should().BeFalse();
    }

    [Fact]
    public async Task 構成すると既定を置き換える()
    {
        var settings = new Dictionary<string, string?>(Enforce)
        {
            [$"{OwnerTokenBinding.AudiencesKey}:0"] = "ast-api",
            [$"{OwnerTokenBinding.ClientsKey}:0"] = "my-bff",
        };

        (await Evaluate(Principal([OwnerRole], ["ast-api"], "my-bff"), AiStockTradingAuthPolicies.OwnerOnly, settings)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole], ["ai-stock-trading-api"], "bff"), AiStockTradingAuthPolicies.OwnerOnly, settings)).Should().BeFalse();
    }

    [Theory]
    [InlineData(OwnerTokenBinding.ModeKey, "Strict")]
    [InlineData(OwnerTokenBinding.AudiencesKey, "ai-stock-trading-api")]
    [InlineData(OwnerTokenBinding.ClientsKey, "bff,ai-stock-trading-owner")]
    public void 構成の誤りは起動で止まる(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        var resolve = () => OwnerTokenBinding.Resolve(configuration);

        resolve.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 段2で空の許可集合は起動で止まる()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(Enforce)
        {
            [$"{OwnerTokenBinding.ClientsKey}:0"] = " ",
        }).Build();

        var resolve = () => OwnerTokenBinding.Resolve(configuration);

        resolve.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 既定は段1_宛先は_platform_api_クライアントは_bff_とボット()
    {
        var settings = OwnerTokenBinding.Resolve(new ConfigurationBuilder().Build());

        settings.Mode.Should().Be(OwnerTokenBindingMode.Report);
        settings.Audiences.Should().Equal("ai-stock-trading-api");
        settings.Clients.Should().Equal("bff", "ai-stock-trading-owner");
    }
}
