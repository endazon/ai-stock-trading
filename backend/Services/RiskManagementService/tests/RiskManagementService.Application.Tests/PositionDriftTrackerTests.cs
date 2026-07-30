using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Application.Tests;

// FR-05, FR-09, FR-10, #292, IADR-0118: 乖離の報告可否。
// 一過性の未反映（発注直後〜約定反映待ち）で鳴らないための連続観測条件と、通知過多を避けるシグネチャ dedup。
public class PositionDriftTrackerTests
{
    private static PositionDriftItem Drift(string symbol, int ledger, int broker, PositionDriftKind kind) =>
        new(symbol, Market.UnitedStates, ledger, broker, kind);

    private static readonly PositionDriftItem[] DriftA =
        [Drift("AAPL", 100, 80, PositionDriftKind.QuantityMismatch)];

    private static readonly PositionDriftItem[] DriftB =
        [Drift("AAPL", 100, 60, PositionDriftKind.QuantityMismatch)];

    [Fact]
    public void 一度きりの乖離は報告しない()
    {
        // 発注直後〜約定ポーリング反映までの一過性のズレを弾く。
        new PositionDriftTracker().ShouldReport(DriftA).Should().BeFalse();
    }

    [Fact]
    public void 連続で同一なら報告する()
    {
        var tracker = new PositionDriftTracker();

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 継続している同一の乖離は再報告しない()
    {
        // 10 分ごとに Discord を叩かない。
        var tracker = new PositionDriftTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeFalse();
    }

    [Fact]
    public void 乖離の内容が変われば連続条件を数え直す()
    {
        var tracker = new PositionDriftTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();

        // 別内容は 1 回目では報告しない（内容が動いている間は一過性の可能性がある）。
        tracker.ShouldReport(DriftB).Should().BeFalse();
        tracker.ShouldReport(DriftB).Should().BeTrue();
    }

    [Fact]
    public void 乖離が解消したら報告しない()
    {
        var tracker = new PositionDriftTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();

        tracker.ShouldReport([]).Should().BeFalse();
        tracker.ShouldReport([]).Should().BeFalse();
    }

    [Fact]
    public void 解消後に同じ乖離が再発したら再び報告する()
    {
        var tracker = new PositionDriftTracker();
        tracker.ShouldReport(DriftA);
        tracker.ShouldReport(DriftA).Should().BeTrue();
        tracker.ShouldReport([]);
        tracker.ShouldReport([]);

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 順序が違うだけの同一集合は同じ乖離として扱う()
    {
        // 列挙順はブローカ応答に依存する。順序差で「内容が変わった」と誤認すると連続条件が永久に満たされない。
        var tracker = new PositionDriftTracker();
        PositionDriftItem[] forward =
        [
            Drift("AAPL", 100, 80, PositionDriftKind.QuantityMismatch),
            Drift("MSFT", 10, 0, PositionDriftKind.LedgerOnly),
        ];
        PositionDriftItem[] reversed = [forward[1], forward[0]];

        tracker.ShouldReport(forward).Should().BeFalse();
        tracker.ShouldReport(reversed).Should().BeTrue();
    }

    [Fact]
    public void 必要な連続回数は構成できる()
    {
        var tracker = new PositionDriftTracker(requiredConsecutiveObservations: 3);

        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeFalse();
        tracker.ShouldReport(DriftA).Should().BeTrue();
    }

    [Fact]
    public void 連続回数の下限は1に丸める()
    {
        // 0・負値で「報告しない」状態を作らない（検知器が黙る設定を許さない）。
        var tracker = new PositionDriftTracker(requiredConsecutiveObservations: 0);

        tracker.ShouldReport(DriftA).Should().BeTrue();
    }
}
