using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-05, IADR-0051, IADR-0093 決定 2, IADR-0323 決定 1, IADR-0328 決定 2, IADR-0332 決定 6, #746:
// gRPC チャネル用の **MSP レルム** s2s トークン供給を inline で作る登録点。
//
// 🔴 固定するのは 3 点である。
//   ① 資格情報が揃っていれば実供給元（client_credentials）を返す
//   ② 揃っていなければ**トークンを出さない供給元**を返す（例外にしない・AST レルムへ落とさない）
//   ③ **DI へ登録しない**（AST レルムの IServiceAccessTokenProvider と TryAdd 衝突すると
//      レルムを跨いでトークンが漏れる。これは「認可が通らない」より悪い＝別レルムの資格で通ってしまう）
public class PlatformRealmTokenProviderTests
{
    private const string SectionName = "LlmGateway:Auth";
    private const string TokenClientName = "llm-gateway-token";

    private static IServiceProvider Services() =>
        new ServiceCollection().AddHttpClient().AddLogging().BuildServiceProvider();

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    // ---- ① 陽性 -------------------------------------------------------------------------------

    [Fact]
    public void 資格情報が揃っていれば実供給元を返す()
    {
        var config = Config(
            ($"{SectionName}:Authority", "http://keycloak:8080/realms/platform"),
            ($"{SectionName}:ClientId", "ai-stock-trading-llm-caller"),
            ($"{SectionName}:ClientSecret", "s3cret"));

        var provider = PlatformRealmAuthExtensions.CreatePlatformRealmTokenProvider(
            Services(), config, SectionName, TokenClientName);

        provider.Should().BeOfType<ClientCredentialsTokenProvider>();
    }

    // ---- ② 陰性対照 ---------------------------------------------------------------------------

    [Theory]
    // 何も無い（既定）。
    [InlineData(null, null, null)]
    // 端点だけ・client だけ・secret 欠落 —— どれか 1 つでも欠けたら「付けない」へ倒す。
    [InlineData("http://keycloak:8080/realms/platform", null, null)]
    [InlineData("http://keycloak:8080/realms/platform", "ai-stock-trading-llm-caller", null)]
    [InlineData(null, "ai-stock-trading-llm-caller", "s3cret")]
    public async Task 資格情報が欠けていればトークンを出さない(string? authority, string? clientId, string? clientSecret)
    {
        var entries = new List<(string, string)>();
        if (authority is not null)
            entries.Add(($"{SectionName}:Authority", authority));
        if (clientId is not null)
            entries.Add(($"{SectionName}:ClientId", clientId));
        if (clientSecret is not null)
            entries.Add(($"{SectionName}:ClientSecret", clientSecret));

        var provider = PlatformRealmAuthExtensions.CreatePlatformRealmTokenProvider(
            Services(), Config([.. entries]), SectionName, TokenClientName);

        provider.Should().BeOfType<NoServiceAccessTokenProvider>();
        // 例外ではなく null（＝メタデータを付けずに送る → 提供側 UNAUTHENTICATED → 既存 fail-safe）。
        (await provider.GetTokenAsync()).Should().BeNull();
    }

    // ---- ③ 🔴 レルムの分離 ---------------------------------------------------------------------

    // AST レルムの `Auth:Authority` / `ServiceAuth:*` へフォールバックしない。
    // 流用すると AST レルム発行トークンを MSP のサービスへ出し、issuer 不一致で 401 になる
    // （IADR-0093 が KB で実測した故障と同型）——「動くように見えて動かない」最悪の形である。
    [Fact]
    public async Task AST_レルムの設定へフォールバックしない()
    {
        var config = Config(
            ("Auth:Authority", "http://keycloak:8080/realms/ai-stock-trading"),
            ("ServiceAuth:ClientId", "ai-stock-trading-service"),
            ("ServiceAuth:ClientSecret", "s3cret"));

        var provider = PlatformRealmAuthExtensions.CreatePlatformRealmTokenProvider(
            Services(), config, SectionName, TokenClientName);

        provider.Should().BeOfType<NoServiceAccessTokenProvider>();
        (await provider.GetTokenAsync()).Should().BeNull();
    }

    // DI へ登録しないこと（呼んでも IServiceAccessTokenProvider が生えない）。
    [Fact]
    public void DI_へは登録しない()
    {
        var services = new ServiceCollection().AddHttpClient().AddLogging();
        var built = services.BuildServiceProvider();
        var config = Config(
            ($"{SectionName}:Authority", "http://keycloak:8080/realms/platform"),
            ($"{SectionName}:ClientId", "ai-stock-trading-llm-caller"),
            ($"{SectionName}:ClientSecret", "s3cret"));

        _ = PlatformRealmAuthExtensions.CreatePlatformRealmTokenProvider(
            built, config, SectionName, TokenClientName);

        services.Should().NotContain(d => d.ServiceType == typeof(IServiceAccessTokenProvider));
        built.GetService<IServiceAccessTokenProvider>().Should().BeNull();
    }
}
