using System.Security.Claims;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// IADR-0011, platform ADR-0004: Keycloak レルムロールの ClaimTypes.Role 展開の検証。
// 冪等性・fail-closed（不正入力ではロールを付与しない）を固定する。
public class KeycloakRolesClaimsTransformationTests
{
    private readonly KeycloakRolesClaimsTransformation _sut = new();

    private static ClaimsPrincipal AuthenticatedWith(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "jwt");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task realm_access_のロールを_Role_クレームへ展開する()
    {
        var principal = AuthenticatedWith(
            new Claim("realm_access", """{"roles":["trading-owner","user"]}"""));

        var result = await _sut.TransformAsync(principal);

        result.IsInRole("trading-owner").Should().BeTrue();
        result.IsInRole("user").Should().BeTrue();
    }

    [Fact]
    public async Task 複数回呼ばれてもロールを重複付与しない()
    {
        // AuthenticateAsync 毎に呼ばれ得るため冪等でなければならない。
        var principal = AuthenticatedWith(
            new Claim("realm_access", """{"roles":["trading-owner"]}"""));

        await _sut.TransformAsync(principal);
        var result = await _sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().ContainSingle(c => c.Value == "trading-owner");
    }

    [Fact]
    public async Task 未認証プリンシパルにはロールを付与しない()
    {
        // fail-closed: 認証されていなければロール展開しない。
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _sut.TransformAsync(anonymous);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"roles":"not-an-array"}""")]
    [InlineData("""{"other":123}""")]
    [InlineData("[]")]
    public async Task 不正または想定外の_realm_access_ではロールを付与しない(string realmAccess)
    {
        // fail-closed: パース不能・想定外形状は無視する。
        var principal = AuthenticatedWith(new Claim("realm_access", realmAccess));

        var result = await _sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task realm_access_クレームが無ければ何もしない()
    {
        var principal = AuthenticatedWith(new Claim("preferred_username", "owner"));

        var result = await _sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }
}
