using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, #1529, IADR-0612: 単体実行向けの認証の既定（コードの既定の Authority・単体実行向けのレルム）を Production で使わせない。
public class StandaloneRealmGuardTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static Action Register(IConfiguration config, string environment) =>
        () => new ServiceCollection().AddLogging().AddAiStockTradingAuth(config, new Env(environment));

    [Fact]
    public void Production_で_Authority_が無ければ起動で止まる()
    {
        Register(Config(), Environments.Production).Should().Throw<InvalidOperationException>();
        Register(Config(("Auth:Authority", "  ")), Environments.Production).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("http://keycloak:8080/realms/ai-stock-trading")]
    [InlineData("http://keycloak:8080/realms/ai-stock-trading/")]
    [InlineData("https://kc.example/realms/AI-STOCK-TRADING")]
    public void Production_で単体実行向けのレルムは_opt_in_が無ければ起動で止まる(string authority)
    {
        Register(Config(("Auth:Authority", authority)), Environments.Production).Should().Throw<InvalidOperationException>();
        Register(Config(("Auth:Authority", authority), (StandaloneRealmGuard.AllowKey, "false")), Environments.Production)
            .Should().Throw<InvalidOperationException>();
        Register(Config(("Auth:Authority", authority), (StandaloneRealmGuard.AllowKey, "true")), Environments.Production)
            .Should().NotThrow("明示の opt-in は通す");
    }

    [Theory]
    [InlineData("http://keycloak:8080/realms/platform")]
    [InlineData("http://keycloak:8080/realms/ai-stock-trading-prod")]
    public void Production_で配備のレルムは通る(string authority) =>
        Register(Config(("Auth:Authority", authority)), Environments.Production).Should().NotThrow();

    [Theory]
    [InlineData("Development")]
    [InlineData("IntegrationTest")]
    [InlineData("Testing")]
    public void Production_以外は従前どおり(string environment)
    {
        Register(Config(), environment).Should().NotThrow();
        Register(Config(("Auth:Authority", StandaloneRealmGuard.DevelopmentDefaultAuthority)), environment).Should().NotThrow();
    }

    // 🔴 認証を登録する全サービスが環境を渡す（渡さないと上の確かめが掛からない）。
    [Fact]
    public void 認証を登録する全サービスの_Program_が環境を渡す()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("backend.slnx から Services を辿る");

        var programs = Directory.EnumerateDirectories(Path.Combine(dir!.FullName, "Services"))
            .Select(d => Path.Combine(d, "Program.cs"))
            .Where(File.Exists)
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(p => p.Text.Contains("AddAiStockTradingAuth(", StringComparison.Ordinal))
            .ToList();

        programs.Should().HaveCountGreaterThanOrEqualTo(8, "前提: 認証を登録するサービスを走査できている");
        programs.Where(p => !p.Text.Contains("AddAiStockTradingAuth(builder.Configuration, builder.Environment)", StringComparison.Ordinal))
            .Select(p => p.Path).Should().BeEmpty();
    }
}
