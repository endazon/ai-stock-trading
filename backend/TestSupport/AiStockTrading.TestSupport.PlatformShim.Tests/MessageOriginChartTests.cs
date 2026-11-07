using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, IADR-0627 決定 6: chart の global.messageOrigin.services（サービス → 渡す鍵）は、コードの要る鍵と一致する。
// 足りなければ段 2 で起動しない。多ければ他の発行元の鍵を持たせる（その Pod から別の発行元を名乗れる）。どちらも赤にする。
public partial class MessageOriginChartTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    [GeneratedRegex(@"^  messageOrigin:\n(?:    .*\n|\s*#.*\n)*?    services:\n((?:      [a-z-]+: \[[a-z, -]*\]\n)+)", RegexOptions.Multiline)]
    private static partial Regex ServicesBlock();

    [GeneratedRegex(@"^      ([a-z-]+): \[([a-z, -]*)\]$", RegexOptions.Multiline)]
    private static partial Regex ServiceLine();

    private static Dictionary<string, string[]> ChartServices()
    {
        var values = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values.yaml")).Replace("\r\n", "\n");
        var block = ServicesBlock().Match(values);
        block.Success.Should().BeTrue("values.yaml に global.messageOrigin.services が要る");
        return ServiceLine().Matches(block.Groups[1].Value).ToDictionary(
            m => m.Groups[1].Value,
            m => m.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void chartが各サービスへ渡す鍵はコードの要る鍵と一致する()
    {
        var chart = ChartServices();
        var expected = new Dictionary<string, string[]>();
        foreach (var issuer in MessageOriginCatalog.Rules.SelectMany(r => r.Issuers.Concat(r.Receivers)).Distinct())
            expected[issuer] = [.. MessageOriginWiringExtensions.RequiredKeys(issuer)];

        chart.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (service, keys) in expected)
            chart[service].Should().BeEquivalentTo(keys, $"chart の {service} に渡す鍵");
    }

    [Fact]
    public void values_cloudは段2で鍵をOpenBaoから写す()
    {
        var cloud = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values-cloud.yaml")).Replace("\r\n", "\n");

        cloud.Should().MatchRegex(@"\n  messageOrigin:\n    mode: Enforce\n");
        cloud.Should().MatchRegex(@"\n  messageOriginKeys:\n    enabled: true\n    targetName: ast-message-origin-keys\n    remoteKey: ast/ast-message-origin-keys\n");
    }
}
