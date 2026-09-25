using AiStockTrading.TestSupport.Composition;
using Xunit;

namespace ConfigurationService.Tests;

// 🔴 NFR, #947, IADR-0397, IADR-0163 決定2: **本番の組み立て（Program.cs）に配線の抜けが無い**ことを機械的に表明する。
// 規則（W0 組めない / W1 省略可能依存の未解決 / W2 渡し忘れ / W3 偽物の陰の本物）と所見への対処は IADR-0397。
// 母集団の実測（develop 3d9b91c2）: roots=6 handlers=0 fields=5。下限はその半分に置く。
public class CompositionWiringGuardTests(ITestOutputHelper output)
{
    // 🔴 無視リストではなくラチェットである（所見が消えた行も赤）。1 行ごとに理由・外す条件・issue 番号を書く。
    private static readonly IReadOnlyDictionary<string, string> Allowlist =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void 本番の組み立てに配線の抜けが無い()
    {
        using var factory = new ConfigurationWorkerWebApplicationFactory();

        var report = factory.InspectComposition(typeof(CompositionWiringGuardTests).Assembly);

        output.WriteLine(report.Describe("ConfigurationService"));
        report.AssertPopulation(minRoots: 3, minFields: 2, minHandlers: 0);
        report.AssertNoUnexpectedFindings(Allowlist);
    }
}
