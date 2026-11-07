using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, NFR-06, FR-14, ADR-0047, IADR-0638 決定 5（IADR-0611 の gRPC 面への延長）: gRPC 面の所有者の門（GrpcOwnerOnly・GrpcOwnerOrService の
// 所有者の分岐）も、REST と同じ段（Auth:OwnerToken:Mode）と許可集合（Auth:OwnerToken:Audiences）でトークンの宛先（aud）を確かめる。
// 段 1（既定 Report）は記録して通し、段 2（Enforce）は通さない。サービスの分岐は変えない（サービスのトークンの段は IADR-0637）。
// 陰性対照: gRPC のポリシーから宛先の要件を外すと、段 2 の「宛先の無いボットのトークン」の行が赤になる。
public class GrpcOwnerAudiencePolicyTests
{
    private const string OwnerRole = "trading-owner";
    private const string BotRole = "trading-bot";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";
    private const string Audience = "ai-stock-trading-api";

    private static ClaimsPrincipal Principal(string[] roles, string? azp, params string[] audiences)
    {
        var claims = new List<Claim> { new("preferred_username", "someone") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (azp is not null) claims.Add(new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, azp));
        claims.AddRange(audiences.Select(a => new Claim(OwnerTokenBinding.AudienceClaim, a)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "preferred_username", ClaimTypes.Role));
    }

    private static async Task<bool> Evaluate(ClaimsPrincipal user, string policy, string? mode)
    {
        var settings = new Dictionary<string, string?>();
        if (mode is not null) settings[OwnerTokenBinding.ModeKey] = mode;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        using var provider = services.BuildServiceProvider();
        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy)).Succeeded;
    }

    public static TheoryData<string> OwnerPolicies =>
        new() { AiStockTradingAuthPolicies.GrpcOwnerOnly, AiStockTradingAuthPolicies.GrpcOwnerOrService };

    [Theory]
    [MemberData(nameof(OwnerPolicies))]
    public async Task 段2では宛先の無いボットのトークンを通さない(string policy)
    {
        (await Evaluate(Principal([OwnerRole], Bot), policy, "Enforce")).Should().BeFalse("宛先（aud）が無い");
        (await Evaluate(Principal([OwnerRole], Bot, "platform-api"), policy, "Enforce")).Should().BeFalse("MSP 共通の宛先は許可集合に無い");
        (await Evaluate(Principal([BotRole], Bot, "account"), policy, "Enforce")).Should().BeFalse("ボット専用のロールも同じ");
    }

    [Theory]
    [MemberData(nameof(OwnerPolicies))]
    public async Task 段2でも宛先が許可集合に在るボットのトークンは通る(string policy)
    {
        (await Evaluate(Principal([OwnerRole], Bot, Audience), policy, "Enforce")).Should().BeTrue();
        (await Evaluate(Principal([BotRole], Bot, "account", Audience), policy, "Enforce")).Should().BeTrue("aud は複数あり得る");
    }

    // 段 1（既定）は従前どおり通す（稼働中の呼び出しを止めない）。azp の門は段に依らず効く。
    [Theory]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOnly, null)]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOnly, "Report")]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOrService, null)]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOrService, "Report")]
    public async Task 段1では宛先の無いボットのトークンを記録して通す(string policy, string? mode)
    {
        (await Evaluate(Principal([OwnerRole], Bot), policy, mode)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole], "bff", Audience), policy, mode)).Should().BeFalse("azp の門は段に依らない");
    }

    // NFR-06, IADR-0638 決定 5（AST#1596 のレビュー）: Owner・Bot・Service のどのロールでもない利用者は、gRPC の 2 つの口のどちらでも、
    // 段に依らず通らない（認証済みで認可が落ちる＝gRPC では PERMISSION_DENIED）。宛先の判定器はこの利用者に Succeed しないので要件が満たされず、
    // 加えて azp の門（GrpcOwnerClientGate.Allows / AllowsOwner）もロールを求めて偽を返す。azp・aud が正しくても同じ。
    [Theory]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOnly, null)]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOnly, "Report")]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOnly, "Enforce")]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOrService, null)]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOrService, "Report")]
    [InlineData(AiStockTradingAuthPolicies.GrpcOwnerOrService, "Enforce")]
    public async Task どのロールでもない利用者はgRPCの両方の口で通らない(string policy, string? mode)
    {
        (await Evaluate(Principal([], Bot, Audience), policy, mode)).Should().BeFalse("ロールが無い");
        (await Evaluate(Principal(["trading-viewer"], Bot, Audience), policy, mode)).Should().BeFalse("許可されていないロール");
    }

    // サービスの分岐は所有者の段の対象外（GrpcOwnerOnly は従前どおり s2s を通さない）。
    [Fact]
    public async Task サービスの分岐は所有者の宛先の段で変わらない()
    {
        (await Evaluate(Principal([ServiceRole], "ai-stock-trading-svc-report"), AiStockTradingAuthPolicies.GrpcOwnerOrService, "Enforce"))
            .Should().BeTrue();
        (await Evaluate(Principal([ServiceRole], Bot, Audience), AiStockTradingAuthPolicies.GrpcOwnerOnly, "Enforce"))
            .Should().BeFalse();
    }
}
