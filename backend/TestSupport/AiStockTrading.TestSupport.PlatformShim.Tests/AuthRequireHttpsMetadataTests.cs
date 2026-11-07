using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, NFR-05, IADR-0629（基盤の MSP:IADR-0693 の受け手）: 認可サーバーへの通信を TLS で検証する切り替え。
// 既定・ローカルは従来どおり（RequireHttpsMetadata=false・追加の発行元なし）。クラウドは chart の global.authTls が
// Auth:RequireHttpsMetadata=true と https の Authority を与える。true なのに https でなければ起動で止まる。
public class AuthRequireHttpsMetadataTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static JwtBearerOptions Options(IConfiguration config)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAiStockTradingAuth(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
    }

    private static Action Register(IConfiguration config) =>
        () => new ServiceCollection().AddLogging().AddAiStockTradingAuth(config);

    [Fact]
    public void 既定ではRequireHttpsMetadataは無効で追加の発行元も無い_クラスタ内はHTTP()
    {
        var options = Options(Config(("Auth:Authority", "http://keycloak:8080/realms/platform")));

        options.RequireHttpsMetadata.Should().BeFalse();
        options.TokenValidationParameters.ValidIssuers.Should().BeNull();
        options.Authority.Should().Be("http://keycloak:8080/realms/platform");
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void Auth_RequireHttpsMetadataを与えるとそれが使われる(string raw, bool expected)
    {
        var options = Options(Config(
            ("Auth:Authority", "https://keycloak:8443/realms/platform"),
            (AuthExtensions.RequireHttpsMetadataConfigKey, raw)));

        options.RequireHttpsMetadata.Should().Be(expected);
        options.Authority.Should().Be("https://keycloak:8443/realms/platform");
    }

    // IADR-0629: https を求めたのに Authority が https でなければ、登録の時点（サービスの起動）で止まる。平文へ黙って倒さない。
    // Authority が無いとき（コードの既定の http）も止まる。
    [Theory]
    [InlineData("http://keycloak:8080/realms/platform")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("keycloak:8443/realms/platform")]
    public void Auth_RequireHttpsMetadataがtrueでAuthorityがhttpsでなければ起動で止まる(string? authority)
    {
        var act = Register(Config(("Auth:Authority", authority), (AuthExtensions.RequireHttpsMetadataConfigKey, "true")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*https*IADR-0629*");
    }

    [Fact]
    public void Auth_RequireHttpsMetadataが真偽値でなければ起動で止まる()
    {
        var act = Register(Config(
            ("Auth:Authority", "https://keycloak:8443/realms/platform"),
            (AuthExtensions.RequireHttpsMetadataConfigKey, "yes")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*true か false*");
    }

    // IADR-0629: 移行の間だけ旧の発行元を足す（カンマ・空白区切り）。空は足さない（既定と同じ）。
    [Fact]
    public void Auth_ValidIssuersを与えると追加の発行元として受ける()
    {
        var options = Options(Config(
            ("Auth:Authority", "https://keycloak:8443/realms/platform"),
            (AuthExtensions.RequireHttpsMetadataConfigKey, "true"),
            (AuthExtensions.ValidIssuersConfigKey, " http://keycloak:8080/realms/platform , http://old:8080/realms/platform ")));

        options.TokenValidationParameters.ValidIssuers.Should().Equal(
            "http://keycloak:8080/realms/platform", "http://old:8080/realms/platform");
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue("発行元の検証は弱めない");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Auth_ValidIssuersが空なら足さない(string raw)
    {
        var options = Options(Config(
            ("Auth:Authority", "https://keycloak:8443/realms/platform"),
            (AuthExtensions.ValidIssuersConfigKey, raw)));

        options.TokenValidationParameters.ValidIssuers.Should().BeNull();
    }
}
