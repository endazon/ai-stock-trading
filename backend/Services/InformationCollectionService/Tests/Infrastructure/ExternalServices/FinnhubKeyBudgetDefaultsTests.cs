using System.Text.Json;
using AwesomeAssertions;
using InformationCollectionService.Infrastructure.ExternalServices;
using Xunit;

namespace InformationCollectionService.Tests;

// FR-01, #1225, IADR-0512: Finnhub の同一鍵の予算の検査器（scripts/check-finnhub-key-budget.js）は、env の無い情報収集を
// scripts/finnhub-key-budget.json に複写したコードの既定値で数える。その複写がコードの既定値と一致することを固定する
// （既定を上げて JSON を直し忘れると、helm.yml の検査は古い値で数えて緑のままになる）。
public class FinnhubKeyBudgetDefaultsTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "backend.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("リポジトリの根（backend/backend.slnx）が見つからない。");
    }

    [Fact]
    public void 予算の検査器が数える情報収集の既定値はFinnhubOptionsの既定と一致する()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "finnhub-key-budget.json")));
        var entry = doc.RootElement.GetProperty("consumers").EnumerateArray()
            .Single(c => c.GetProperty("deployment").GetString() == "information-collection-service");

        entry.GetProperty("rateEnv").GetString().Should().Be("Collection__Source__Finnhub__RateLimitPerMinute");
        entry.GetProperty("codeDefault").GetInt32().Should().Be(new CollectionSourceOptions().Finnhub.RateLimitPerMinute);
    }
}
