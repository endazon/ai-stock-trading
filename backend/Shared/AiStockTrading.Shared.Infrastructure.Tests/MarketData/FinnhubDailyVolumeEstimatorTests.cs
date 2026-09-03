using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.MarketData;

// FR-01, ADR-0031（計画）決定2〜4, IADR-0292: Finnhub の日次総量見積り（純関数）の境界を固定する。
// ADR-0031 決定2: 「監視銘柄数を日次上限から逆算する」統制は撤回しない——分次の自制レートは瞬間的な
// 要求レートしか保証せず、日次の総量（銘柄数 × 1巡回あたりの要求数 × 1日の巡回回数）は別に見積もる。
public class FinnhubDailyVolumeEstimatorTests
{
    [Fact]
    public void CyclesPerDayは巡回間隔から切り捨てで算出する()
    {
        // 30 分間隔（IADR-0292 の InformationCollectionService 既定）＝ 48 巡回/日。
        FinnhubDailyVolumeEstimator.CyclesPerDay(1800).Should().Be(48);
        // 60 秒間隔（MarketMonitorService 既定）＝ 1440 巡回/日。
        FinnhubDailyVolumeEstimator.CyclesPerDay(60).Should().Be(1440);
        // 割り切れない間隔は切り捨てる（超過側ではなく不足側へ倒す。過大評価を避ける）。
        FinnhubDailyVolumeEstimator.CyclesPerDay(86399).Should().Be(1);
    }

    [Fact]
    public void CyclesPerDayは非正値を拒否する()
    {
        var act1 = () => FinnhubDailyVolumeEstimator.CyclesPerDay(0);
        var act2 = () => FinnhubDailyVolumeEstimator.CyclesPerDay(-1);

        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ちょうど暫定上限なら超過ではない()
    {
        // 6 銘柄 × 1 要求/巡回 × 50 巡回/日 = 300（ADR-0031 決定2 の桁確認表と同じ形）。
        var process = new FinnhubDailyVolumeEstimator.ProcessVolume("svc", "key-a", 6, 1, 50);

        var result = FinnhubDailyVolumeEstimator.Evaluate(process, provisionalDailyLimit: 300);

        result.EstimatedDailyRequests.Should().Be(300);
        result.Verdict.Should().Be(FinnhubDailyVolumeEstimator.Verdict.Within);
    }

    [Fact]
    public void 暫定上限を1回でも超えると超過と判定する()
    {
        var process = new FinnhubDailyVolumeEstimator.ProcessVolume("svc", "key-a", 6, 1, 51);

        var result = FinnhubDailyVolumeEstimator.Evaluate(process, provisionalDailyLimit: 300);

        result.EstimatedDailyRequests.Should().Be(306);
        result.Verdict.Should().Be(FinnhubDailyVolumeEstimator.Verdict.Exceeds);
        result.ExceedRatio.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void 同一鍵グループの複数プロセスは合算して判定する()
    {
        // ADR-0031 決定4: 同一鍵を共有する全プロセスの見積りは合算する。
        // 情報収集 150 + 市況4サービス(各 40) = 310 > 300（暫定上限超過）。
        var processes = new[]
        {
            new FinnhubDailyVolumeEstimator.ProcessVolume("information-collection", "shared-key", 5, 2, 15),
            new FinnhubDailyVolumeEstimator.ProcessVolume("market-monitor", "shared-key", 40, 1, 1),
        };

        var results = FinnhubDailyVolumeEstimator.Evaluate(processes, provisionalDailyLimit: 300);

        results.Should().ContainSingle();
        results[0].ApiKeyGroup.Should().Be("shared-key");
        results[0].EstimatedDailyRequests.Should().Be(150 + 40);
        results[0].Verdict.Should().Be(FinnhubDailyVolumeEstimator.Verdict.Within);
        results[0].ProcessNames.Should().BeEquivalentTo(["information-collection", "market-monitor"]);
    }

    [Fact]
    public void 鍵が別のプロセスは合算せず独立に判定する()
    {
        // 単独では上限内でも、誤って合算すれば超過になる組み合わせ。鍵が別なら合算しないため両方 Within のまま。
        var processes = new[]
        {
            new FinnhubDailyVolumeEstimator.ProcessVolume("information-collection", "key-collection", 5, 2, 15),
            new FinnhubDailyVolumeEstimator.ProcessVolume("market-monitor", "key-marketdata", 40, 1, 1),
        };

        var results = FinnhubDailyVolumeEstimator.Evaluate(processes, provisionalDailyLimit: 200);

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.ApiKeyGroup == "key-collection" && r.EstimatedDailyRequests == 150
            && r.Verdict == FinnhubDailyVolumeEstimator.Verdict.Within);
        results.Should().Contain(r => r.ApiKeyGroup == "key-marketdata" && r.EstimatedDailyRequests == 40
            && r.Verdict == FinnhubDailyVolumeEstimator.Verdict.Within);
    }

    [Fact]
    public void 合算した結果が超過なら該当グループだけがExceedsになる()
    {
        var processes = new[]
        {
            new FinnhubDailyVolumeEstimator.ProcessVolume("a", "shared", 100, 1, 4), // 400
            new FinnhubDailyVolumeEstimator.ProcessVolume("b", "isolated", 1, 1, 1), // 1
        };

        var results = FinnhubDailyVolumeEstimator.Evaluate(processes, provisionalDailyLimit: 300);

        results.Single(r => r.ApiKeyGroup == "shared").Verdict.Should().Be(FinnhubDailyVolumeEstimator.Verdict.Exceeds);
        results.Single(r => r.ApiKeyGroup == "isolated").Verdict.Should().Be(FinnhubDailyVolumeEstimator.Verdict.Within);
    }

    [Fact]
    public void 既知合計値からの簡易評価は内訳を経由した評価と同じ結果になる()
    {
        var viaBreakdown = FinnhubDailyVolumeEstimator.Evaluate(
            new FinnhubDailyVolumeEstimator.ProcessVolume("svc", "self", 10, 1, 50), provisionalDailyLimit: 300);
        var viaTotal = FinnhubDailyVolumeEstimator.Evaluate(500, provisionalDailyLimit: 300, "svc");

        viaTotal.EstimatedDailyRequests.Should().Be(viaBreakdown.EstimatedDailyRequests);
        viaTotal.Verdict.Should().Be(viaBreakdown.Verdict);
        viaTotal.ExceedRatio.Should().Be(viaBreakdown.ExceedRatio);
    }

    [Fact]
    public void 負の入力は拒否する()
    {
        var act = () => new FinnhubDailyVolumeEstimator.ProcessVolume("svc", "key", -1, 1, 1).EstimatedDailyRequests;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void プロセス0件の合算は空を返す()
    {
        FinnhubDailyVolumeEstimator.Evaluate([], provisionalDailyLimit: 300).Should().BeEmpty();
    }
}
