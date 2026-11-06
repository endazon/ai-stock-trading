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

    // ---- イメージのビルド時の codegen（#1557。IADR-0612 の 2026-11-06 追記） ----

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "Dockerfile")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリのルート（backend/Dockerfile）が見つからない");
    }

    // backend/Dockerfile の `codegen write` の行（コメントでない行）。
    private static string CodegenLine() =>
        File.ReadLines(Path.Combine(RepoRoot(), "backend", "Dockerfile"))
            .Single(l => !l.TrimStart().StartsWith('#') && l.Contains("codegen write", StringComparison.Ordinal));

    // 🔴 build 段は環境名が Production で Authority を持たない。codegen の行に仮の Authority が無いと、守りがホストの組み立てで止める
    // （MSP#2463 の Images の赤。exit 134）。仮の値は名前解決しない .invalid で、単体実行向けのレルムを指さず、Production の守りを通る。
    [Fact]
    public void Dockerfile_の_codegen_の段は仮の_Authority_を渡し_Production_の守りを通る()
    {
        var match = System.Text.RegularExpressions.Regex.Match(CodegenLine(), @"\bAuth__Authority=(\S+)\s+dotnet run ");
        match.Success.Should().BeTrue("codegen の行の dotnet run に Auth__Authority を前置する");
        var authority = match.Groups[1].Value;

        new Uri(authority).Host.Should().EndWith(".invalid", "名前解決しない予約の TLD（どこへも接続しない）");
        StandaloneRealmGuard.IsStandaloneRealm(authority).Should().BeFalse("単体実行向けのレルムを指さない");
        Register(Config(("Auth:Authority", authority)), Environments.Production).Should().NotThrow();
    }

    // 仮の値は codegen の行の中だけ。稼働のイメージ（ENV）へ残すと、配備の値が無くても守りを通ってしまう。
    [Fact]
    public void Dockerfile_は仮の_Authority_を_ENV_に置かない_守りは弱めない()
    {
        File.ReadLines(Path.Combine(RepoRoot(), "backend", "Dockerfile"))
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Should().NotContain(l => l.TrimStart().StartsWith("ENV", StringComparison.Ordinal) && l.Contains("Auth__Authority", StringComparison.Ordinal));
        Register(Config(), Environments.Production).Should().Throw<InvalidOperationException>("稼働の起動では従来どおり配備の値が要る");
    }
}
