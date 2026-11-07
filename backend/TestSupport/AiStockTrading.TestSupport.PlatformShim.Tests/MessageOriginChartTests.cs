using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, FR-10, IADR-0633 決定 3: chart の global.messageOrigin（signers ＝秘密の鍵を渡す発行元・verifiers ＝受け手 → 公開の鍵を渡す発行元）は、
// コードの要る鍵と一致する。足りなければ段 2 で起動しない。秘密の鍵が多ければ発行しない Pod に秘密を置く。どちらも赤にする。
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

    [GeneratedRegex(@"^  messageOrigin:\n((?:(?:    .*|\s*#.*|)\n)+)", RegexOptions.Multiline)]
    private static partial Regex MessageOriginBlock();

    [GeneratedRegex(@"^    signers: \[([a-z, -]*)\]$", RegexOptions.Multiline)]
    private static partial Regex SignersLine();

    [GeneratedRegex(@"^    verifiers:\n((?:(?:      [a-z-]+: \[[a-z, -]*\]|\s*#.*)\n)+)", RegexOptions.Multiline)]
    private static partial Regex VerifiersBlock();

    [GeneratedRegex(@"^      ([a-z-]+): \[([a-z, -]*)\]$", RegexOptions.Multiline)]
    private static partial Regex ServiceLine();

    private static string[] List(string text) => text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static (string[] Signers, Dictionary<string, string[]> Verifiers) Chart()
    {
        var values = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values.yaml")).Replace("\r\n", "\n");
        var block = MessageOriginBlock().Match(values);
        block.Success.Should().BeTrue("values.yaml に global.messageOrigin が要る");
        var signers = SignersLine().Match(block.Groups[1].Value);
        signers.Success.Should().BeTrue("global.messageOrigin.signers が要る");
        var verifiers = VerifiersBlock().Match(block.Groups[1].Value);
        verifiers.Success.Should().BeTrue("global.messageOrigin.verifiers が要る");
        return (
            List(signers.Groups[1].Value),
            ServiceLine().Matches(verifiers.Groups[1].Value).ToDictionary(m => m.Groups[1].Value, m => List(m.Groups[2].Value)));
    }

    private static IEnumerable<string> AllServices() =>
        MessageOriginCatalog.Rules.SelectMany(r => r.Issuers.Concat(r.Receivers))
            .Concat(MessageOriginCatalog.RecordOnlyServices.Select(r => r.Service))
            .Distinct(StringComparer.Ordinal);

    [Fact]
    public void chartの秘密の鍵は発行元のサービスにだけ渡す()
    {
        var (signers, _) = Chart();

        signers.Should().BeEquivalentTo(AllServices().Where(MessageOriginWiringExtensions.RequiresSigningKey));
        signers.Should().BeEquivalentTo(MessageOriginCatalog.AllIssuers);
    }

    [Fact]
    public void chartが受け手へ渡す公開の鍵はコードの要る鍵と一致する()
    {
        var (_, verifiers) = Chart();
        var expected = AllServices()
            .Select(s => (Service: s, Keys: MessageOriginWiringExtensions.RequiredVerifyKeys(s)))
            .Where(x => x.Keys.Count > 0)
            .ToDictionary(x => x.Service, x => x.Keys);

        verifiers.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (service, keys) in expected)
            verifiers[service].Should().BeEquivalentTo(keys, $"chart の {service} に渡す公開の鍵");
    }

    // 入れ替えの間の前の公開の鍵は、既定では誰にも渡さない（空）。
    [Fact]
    public void 前の公開の鍵の既定は空()
    {
        var values = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values.yaml")).Replace("\r\n", "\n");
        MessageOriginBlock().Match(values).Groups[1].Value.Should().MatchRegex(@"(?m)^    previousVerify: \[\]$");
    }

    [Fact]
    public void values_cloudは段2で2つの鍵をOpenBaoから写し旧い鍵を使わない()
    {
        var cloud = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values-cloud.yaml")).Replace("\r\n", "\n");

        cloud.Should().MatchRegex(@"\n  messageOrigin:\n    mode: Enforce\n");
        cloud.Should().MatchRegex(
            @"\n  messageOriginSigningKeys:\n    enabled: true\n    targetName: ast-message-origin-signing-keys\n    remoteKey: ast/ast-message-origin-signing-keys\n");
        cloud.Should().MatchRegex(
            @"\n  messageOriginVerifyKeys:\n    enabled: true\n    targetName: ast-message-origin-verify-keys\n    remoteKey: ast/ast-message-origin-verify-keys\n");
        cloud.Should().NotContain("messageOriginKeys:");
        cloud.Should().NotMatchRegex(@"\n +legacyHmac:", "values-cloud は旧い共有の鍵を有効にしない");
    }

    // 🔴 NFR, FR-09, IADR-0638 決定 3: 段の下げは、新しく足した受け手（通知）だけ。既定の values は空、values-cloud は通知の 1 行だけで、
    //   発行元（署名する側）と既存の受け手を下げない。
    [Fact]
    public void 段の下げは既定で空でvalues_cloudは通知だけを段1にする()
    {
        var values = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values.yaml")).Replace("\r\n", "\n");
        MessageOriginBlock().Match(values).Groups[1].Value.Should().MatchRegex(@"(?m)^    modeOverrides: \{\}$");

        var cloud = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "helm", "ai-stock-trading", "values-cloud.yaml")).Replace("\r\n", "\n");
        var block = MessageOriginBlock().Match(cloud);
        block.Success.Should().BeTrue();
        var overrides = Regex.Matches(block.Groups[1].Value, @"(?m)^      ([a-z-]+): ([A-Za-z]+)$")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        overrides.Should().BeEquivalentTo(new Dictionary<string, string> { [MessageOriginCatalog.Notification] = "Report" });
        MessageOriginWiringExtensions.RequiresSigningKey(MessageOriginCatalog.Notification).Should().BeFalse("下げるのは署名しない受け手だけ");
    }
}
