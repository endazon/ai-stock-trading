using AwesomeAssertions;
using Xunit;

namespace ReportService.Domain.Tests;

// FR-06, FR-20, #338, INDEX 決定34, 06_daytrading-review §4.2, 04_report-templates 月報 §6.2:
// OpenD 稼働率の分布（純関数）を固定する。
//
// 🔴 計画の明文（決定34）: 「**その日の通常取引時間の 50% 以上が稼働していれば 1 日**として数え、
// 50% 未満は算入しない。」 **50% ちょうどは算入側**である（「以上」）。
// 境界の向きを誤ると Stage 1 の期間カウントが 1 日ずつずれ、昇格判定の根拠が狂う。
public class OpenDUptimeAggregatorTests
{
    private static OpenDUptimeDay Day(int day, double ratio) => new(new DateOnly(2026, 8, day), (decimal)ratio);

    // --- 境界値テーブル ---

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.4999, false)]
    [InlineData(0.50, true)]   // 🔴 境界: ちょうど 50% は算入する
    [InlineData(0.51, true)]
    [InlineData(1.0, true)]
    public void 稼働率五十パーセント以上を算入する(double ratio, bool counted)
    {
        OpenDUptimeAggregator.IsCounted((decimal)ratio).Should().Be(counted);
    }

    [Fact]
    public void 分布は百パーセントと五十から九十九と五十未満に分ける()
    {
        var record = new OpenDUptimeRecord(
        [
            Day(3, 1.0), Day(4, 1.0),
            Day(5, 0.50), Day(6, 0.99),
            Day(7, 0.49), Day(10, 0.0),
        ]);

        var d = OpenDUptimeAggregator.Distribution(record);

        d.FullDays.Should().Be(2);
        d.PartialCountedDays.Should().Be(2);
        d.NotCountedDays.Should().Be(2);
        d.CountedDays.Should().Be(4);
    }

    // 観測が 1 日も無い期間は「すべて 0 日」である。**これは「照会できていない」とは別**であり、
    // 未供給は OpenDUptimeRecord そのものが null であることで表す（描画側で固定する）。
    [Fact]
    public void 観測が無ければ全ての区分がゼロ日である()
    {
        var d = OpenDUptimeAggregator.Distribution(new OpenDUptimeRecord([]));

        d.FullDays.Should().Be(0);
        d.PartialCountedDays.Should().Be(0);
        d.NotCountedDays.Should().Be(0);
        d.CountedDays.Should().Be(0);
    }

    // 100% を超える値（観測の重複計上など）は 100% と同じく算入側へ数える（安全側ではなく事実側）。
    [Fact]
    public void 百パーセントを超える稼働率も満稼働として数える()
    {
        OpenDUptimeAggregator.Distribution(new OpenDUptimeRecord([Day(3, 1.2)])).FullDays.Should().Be(1);
    }

    // 06_daytrading-review §4.1: Stage 1 の目標は 60 営業日。定数が動くと合格判定の読みが変わる。
    [Fact]
    public void ステージ一の目標日数は六十日である()
    {
        OpenDUptimeAggregator.Stage1TargetDays.Should().Be(60);
        OpenDUptimeAggregator.Stage1CountingThreshold.Should().Be(0.50m);
    }
}
