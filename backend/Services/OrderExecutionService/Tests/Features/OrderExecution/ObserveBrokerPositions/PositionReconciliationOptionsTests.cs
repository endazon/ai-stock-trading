using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// #292, FR-05, IADR-0118: 建玉突合の構成。既定有効・間隔のクランプ。
public class PositionReconciliationOptionsTests
{
    [Fact]
    public void 既定は有効で間隔は10分()
    {
        // 検知器を既定オフで出荷することは「乖離が見えない状態」を既定にすることを意味する（IADR-0118）。
        var options = new PositionReconciliationOptions();

        options.Enabled.Should().BeTrue();
        options.Interval.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Theory]
    [InlineData(0, 600)]     // 未設定・非正は既定へ
    [InlineData(-1, 600)]
    [InlineData(30, 60)]     // 下限クランプ（照会の連打を防ぐ）
    [InlineData(60, 60)]
    [InlineData(900, 900)]
    [InlineData(3600, 3600)]
    [InlineData(86400, 3600)] // 上限クランプ（事実上止まっている設定を作らせない）
    public void 間隔は範囲へクランプされる(int configured, int expectedSeconds)
    {
        new PositionReconciliationOptions { IntervalSeconds = configured }
            .Interval.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // T-10-1745, FR-10, #1093, IADR-0459 決定1: 負の値は 0（遅らせない）、巡回間隔を超える値は巡回間隔へ収める。
    [Theory]
    [InlineData(null, 600, 20)]     // 未設定は既定 20 秒
    [InlineData(-5, 600, 0)]        // 負は 0（遅らせない）
    [InlineData(0, 600, 0)]         // 0 は遅らせない（明示の無効化）
    [InlineData(45, 600, 45)]       // 範囲内はそのまま
    [InlineData(9999, 600, 600)]    // 巡回間隔を超える値は巡回間隔
    [InlineData(120, 60, 60)]       // 巡回間隔を縮めれば上限も縮む
    public void T_10_1745_初回の遅延の設定を収める(int? configured, int intervalSeconds, int expectedSeconds)
    {
        var options = new PositionReconciliationOptions { IntervalSeconds = intervalSeconds };
        if (configured is { } value) options.InitialDelaySeconds = value;

        options.InitialDelay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }
}
