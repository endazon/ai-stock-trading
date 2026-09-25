using AiStockTrading.TestSupport.Composition;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 NFR, #947, IADR-0397, IADR-0163 決定2: **本番の組み立て（Program.cs）に配線の抜けが無い**ことを機械的に表明する。
// 規則（W0 組めない / W1 省略可能依存の未解決 / W2 渡し忘れ / W3 偽物の陰の本物）と所見への対処は IADR-0397。
//
// 🔴 **時価評価の有効／無効で組み立てが違う**ため、両方の構成で走らせる。既定（values.yaml）は無効、
// 稼働中の経路B（values-local.yaml の MarketData__EnableMarkToMarket=true）は有効である。
// 母集団の実測（develop 3d9b91c2）: 既定 roots=61 handlers=23 fields=182 / 時価評価 roots=62 handlers=23 fields=188。
public class CompositionWiringGuardTests(ITestOutputHelper output)
{
    // 🔴 無視リストではなくラチェットである（所見が消えた行も赤）。1 行ごとに理由・外す条件・issue 番号を書く。
    private static readonly IReadOnlyDictionary<string, string> DefaultAllowlist =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["W2 RiskManagementService.Infrastructure.ExternalServices.LedgerPortfolioStateProvider.currentPrices"] =
                "#81 / IADR-0066: 時価評価の既定は無効（fail-safe。含み 0・DD 0 の現行挙動）であり、Program.cs は"
                + "MarketData:EnableMarkToMarket=false のとき現在値ソースを意図して渡さない（ICurrentPriceSource の登録は"
                + "構成を問わず在る）。結線は時価評価を有効にした構成のガードが検査する。"
                + "外す条件: 無効時に ICurrentPriceSource を登録しない形へ改めたとき、または時価評価を常時有効にしたとき。",
        };

    private static readonly IReadOnlyDictionary<string, string> MarkToMarketAllowlist =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void 本番の組み立てに配線の抜けが無い_既定構成()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var report = factory.InspectComposition(typeof(CompositionWiringGuardTests).Assembly);
        output.WriteLine(report.Describe("RiskManagementService[default]"));
        report.AssertPopulation(minRoots: 30, minFields: 91, minHandlers: 11);
        report.AssertNoUnexpectedFindings(DefaultAllowlist);
    }

    [Fact]
    public void 本番の組み立てに配線の抜けが無い_時価評価を有効にした構成()
    {
        using var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings = { ["MarketData:EnableMarkToMarket"] = "true" },
        };
        var report = factory.InspectComposition(typeof(CompositionWiringGuardTests).Assembly);
        output.WriteLine(report.Describe("RiskManagementService[mark-to-market]"));
        report.AssertPopulation(minRoots: 31, minFields: 94, minHandlers: 11);
        report.AssertNoUnexpectedFindings(MarkToMarketAllowlist);
    }
}
