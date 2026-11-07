using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Xunit;

namespace ConfigurationService.Tests;

// 🔴 NFR, IADR-0637: 本物の組み立てで、サービスのロール（trading-service）で通り得る経路はどれも口の名前（.RequireServiceCallers / [ServiceCallers]）を持つ。
// 付け忘れると段 Enforce でサービスから 403 になる（閉じる向き）。名前を付けない経路は下の除外表に理由とともに置く。
public class ServiceCallerCoverageTests
{
    // 名前を付けない経路（"METHOD /テンプレート" → 理由）。
    private static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public async Task サービスのロールで通り得る経路はどれも口の名前を持つ()
    {
        using var factory = new ConfigurationWorkerWebApplicationFactory();
        _ = factory.CreateClient();
        var unnamed = await ServiceCallerCoverage.UnnamedServiceRoutesAsync(factory.Services);
        string.Join(", ", unnamed.Where(r => !Excluded.ContainsKey(r))).Should().BeEmpty("口の名前（ServiceCallerNames）と既定の一覧を足す。サービスに開かないなら認可をサービスの外にする");
        string.Join(", ", Excluded.Keys.Where(k => !unnamed.Contains(k))).Should().BeEmpty("除外表に、もう名前を持つ（か無い）経路が残っている");
    }
}
