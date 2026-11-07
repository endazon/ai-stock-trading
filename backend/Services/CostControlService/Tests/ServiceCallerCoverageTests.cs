using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using AwesomeAssertions;
using Xunit;

namespace CostControlService.Tests;

// 🔴 NFR, IADR-0637: 本物の組み立てで、サービスのロール（trading-service）で通り得る経路はどれも口の名前（.RequireServiceCallers / [ServiceCallers]）を持つ。
// 付け忘れると段 Enforce でサービスから 403 になる（閉じる向き）。名前を付けない経路は下の除外表に理由とともに置く。
public class ServiceCallerCoverageTests
{
    // 名前を付けない経路（"METHOD /テンプレート" → 理由）。
    private static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 認可は OwnerOrService（IADR-0051）だが、サービスの呼び手の実装が無い（ExternalServices に /costs/review・/costs/usage を叩くものが無い。
        // 作業仕様書 20261107_s2s-per-service-clients の母集合）。段 Enforce ではサービスから 403（閉じる向き）。呼ぶサービスができたら名前を付ける。
        ["GET /costs/review"] = "サービスの呼び手が無い（所有者の照会だけ）",
        ["GET /costs/usage"] = "サービスの呼び手が無い（所有者の照会だけ。月報への供給は未実装）",
    };

    [Fact]
    public async Task サービスのロールで通り得る経路はどれも口の名前を持つ()
    {
        using var factory = new CostControlWorkerWebApplicationFactory();
        _ = factory.CreateClient();
        var unnamed = await ServiceCallerCoverage.UnnamedServiceRoutesAsync(factory.Services);
        string.Join(", ", unnamed.Where(r => !Excluded.ContainsKey(r))).Should().BeEmpty("口の名前（ServiceCallerNames）と既定の一覧を足す。サービスに開かないなら認可をサービスの外にする");
        string.Join(", ", Excluded.Keys.Where(k => !unnamed.Contains(k))).Should().BeEmpty("除外表に、もう名前を持つ（か無い）経路が残っている");
    }
}
