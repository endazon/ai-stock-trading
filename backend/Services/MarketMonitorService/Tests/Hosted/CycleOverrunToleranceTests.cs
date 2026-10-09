using AwesomeAssertions;
using MarketMonitorService.Hosted;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-04, NFR-01, ADR-0043 決定 2 (b), #1281, IADR-0513: 巡回の所要の Warning に足す余裕は、Finnhub のときだけ
// 限流器の 1 要求ぶんの送出間隔（⌈1 分 ÷ r⌉。限流器と同じティック切り上げ）、他の提供元・未設定は 0。
public class CycleOverrunToleranceTests
{
    // T-10-2471
    [Theory]
    [InlineData("Finnhub", 12, 50_000_000L)] // 5 秒
    [InlineData(" finnhub ", 12, 50_000_000L)] // 大小文字・前後の空白を無視する
    [InlineData("FINNHUB", 7, 85_714_286L)] // 60 ÷ 7 ＝ 8.57142857… 秒をティックで切り上げ
    [InlineData("Finnhub", 0, 600_000_000L)] // 0 以下は 1 回/分へ寄せる（60 秒）
    [InlineData("Finnhub", 60, 10_000_000L)] // 1 秒
    [InlineData("moomoo", 12, 0L)]
    [InlineData("Fake", 12, 0L)]
    [InlineData(null, 12, 0L)]
    [InlineData("", 12, 0L)]
    public void T_10_2471_余裕はFinnhubのときだけ自制レートの1要求ぶんの送出間隔(string? provider, int requestsPerMinute, long expectedTicks)
    {
        CycleOverrunTolerance.For(provider, requestsPerMinute).Value
            .Should().Be(TimeSpan.FromTicks(expectedTicks));
    }
}
