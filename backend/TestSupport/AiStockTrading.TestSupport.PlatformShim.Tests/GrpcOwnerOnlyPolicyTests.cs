using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// T-10-1727, NFR-06, FR-14, ADR-0047 決定 1〜3, IADR-0449 決定 2, #753（段 5）:
// **所有者限定の gRPC 面の門（`GrpcOwnerOnly`）**。本物の登録（AddAiStockTradingAuth）が作るポリシーを `IAuthorizationService` で評価する。
// trading-owner ∧ azp がボットの機密クライアント、のときだけ通す。🔴 REST の OwnerOnly と同じく s2s（trading-service だけ）は通さない。
// 既存の `GrpcOwnerOrService`（T-10-1723）と REST の `OwnerOnly` は変わらない。
public class GrpcOwnerOnlyPolicyTests
{
    private const string OwnerRole = "trading-owner";
    private const string ServiceRole = "trading-service";
    private const string Bot = "ai-stock-trading-owner";

    private static ClaimsPrincipal Principal(string[] roles, params string[] azps)
    {
        var claims = new List<Claim> { new("preferred_username", "someone") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(azps.Select(a => new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, a)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", "preferred_username", ClaimTypes.Role));
    }

    private static async Task<bool> Evaluate(
        ClaimsPrincipal user, string policy = AiStockTradingAuthPolicies.GrpcOwnerOnly, IDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingAuth(new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        using var provider = services.BuildServiceProvider();
        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource: null, policy)).Succeeded;
    }

    [Fact]
    public async Task T_10_1727_ボットのトークンは通る()
    {
        (await Evaluate(Principal([OwnerRole], Bot))).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole, ServiceRole], Bot))).Should().BeTrue("サービスのロールを併せ持っても所有者の分岐で通る");
    }

    // 🔴 否定の試験: s2s（サービスのロールだけ）は、azp が何であっても通さない（REST の OwnerOnly と同じ最小権限）。
    [Theory]
    [InlineData("ai-stock-trading-svc")]
    [InlineData(Bot)]
    public async Task T_10_1727_サービスのロールだけでは通さない(string azp)
    {
        (await Evaluate(Principal([ServiceRole], azp))).Should().BeFalse();
        (await Evaluate(Principal([ServiceRole]))).Should().BeFalse();
    }

    // 🔴 否定の試験: 人の利用者のトークン（azp が BFF・公開クライアント）・変種・azp の欠落・複数。
    [Theory]
    [InlineData("ai-stock-trading-dev")]
    [InlineData("bff")]
    [InlineData("AI-STOCK-TRADING-OWNER")]
    [InlineData("ai-stock-trading-owner-bff")]
    [InlineData(" ai-stock-trading-owner")]
    [InlineData("")]
    public async Task T_10_1727_所有者のロールでも呼び出し元がボットでなければ拒否(string azp)
    {
        (await Evaluate(Principal([OwnerRole], azp))).Should().BeFalse($"azp='{azp}'");
    }

    [Fact]
    public async Task T_10_1727_azpの欠落と複数とロール無しと未認証は拒否()
    {
        (await Evaluate(Principal([OwnerRole]))).Should().BeFalse("azp が無い");
        (await Evaluate(Principal([OwnerRole], Bot, "bff"))).Should().BeFalse("azp が 2 つ");
        (await Evaluate(Principal(["some-unrelated-role"], Bot))).Should().BeFalse("ロールが無い");
        (await Evaluate(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, OwnerRole), new Claim(GrpcOwnerClientGate.AuthorizedPartyClaim, Bot)])))).Should().BeFalse("未認証");
    }

    // 許可集合は GrpcOwnerOrService と同じ構成（`Auth:GrpcOwnerClients`）から採る（2 つの門で集合が割れない）。
    [Fact]
    public async Task T_10_1727_許可集合はGrpcOwnerOrServiceと同じ構成から採る()
    {
        var settings = new Dictionary<string, string?> { ["Auth:GrpcOwnerClients:0"] = "other-bot" };

        (await Evaluate(Principal([OwnerRole], "other-bot"), settings: settings)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole], Bot), settings: settings)).Should().BeFalse("構成は既定を置き換える");
        (await Evaluate(Principal([OwnerRole], "other-bot"), AiStockTradingAuthPolicies.GrpcOwnerOrService, settings)).Should().BeTrue();
    }

    // 陰性対照: 既存の門は変わらない（GrpcOwnerOrService は s2s を通し、REST の OwnerOnly は azp を見ない）。
    [Fact]
    public async Task T_10_1727_既存の門は変わらない()
    {
        (await Evaluate(Principal([ServiceRole]), AiStockTradingAuthPolicies.GrpcOwnerOrService)).Should().BeTrue();
        (await Evaluate(Principal([OwnerRole]), AiStockTradingAuthPolicies.OwnerOnly)).Should().BeTrue("REST の面は azp を見ない");
        (await Evaluate(Principal([ServiceRole]), AiStockTradingAuthPolicies.OwnerOnly)).Should().BeFalse();
    }
}
