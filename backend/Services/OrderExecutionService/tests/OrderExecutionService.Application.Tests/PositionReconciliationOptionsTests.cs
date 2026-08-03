using AiStockTrading.OrderExecution.Application.Reconciliation;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Application.Tests;

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
}
