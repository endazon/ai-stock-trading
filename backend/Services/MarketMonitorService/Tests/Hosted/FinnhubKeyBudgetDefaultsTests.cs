using System.Text.Json;
using AwesomeAssertions;
using MarketMonitorService.Hosted;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, #1225, IADR-0512: Finnhub の予算の検査器（scripts/check-finnhub-key-budget.js）は、市場監視の 1 巡回に収まる要求数
// （自制レート × 巡回間隔 ÷ 60）を、env の無い巡回間隔について scripts/finnhub-key-budget.json に複写したコードの既定値で数える。
// その複写がコードの既定値と一致することを固定する（既定を変えて JSON を直し忘れると、検査は古い値で数えて緑のままになる）。
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
    public void 予算の検査器が数える巡回間隔の既定値はMonitorOptionsの既定と一致する()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "finnhub-key-budget.json")));
        var cycleFit = doc.RootElement.GetProperty("cycleFit");

        cycleFit.GetProperty("deployment").GetString().Should().Be("market-monitor-service");
        cycleFit.GetProperty("pollEnv").GetString().Should().Be("Monitor__PollIntervalSeconds");
        cycleFit.GetProperty("pollCodeDefault").GetInt32().Should().Be(new MonitorOptions().PollIntervalSeconds);
    }
}
