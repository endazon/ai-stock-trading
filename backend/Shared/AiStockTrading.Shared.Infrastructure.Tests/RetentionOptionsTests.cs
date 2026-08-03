using AiStockTrading.Shared.Contracts.Operations;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// NFR（運用）, #137, IADR-0059: パージジョブの構成。設定ミスを構造で吸収する性質を
// RetentionPolicy の下限クランプと同じ厳密さで固定する（claude-review 🟡 の指摘）。
//
// 保持期間（RetentionDays）の下限は RetentionPolicy 側が担保する（RetentionPolicyTests）。
// 本テストは巡回間隔・バッチサイズのクランプと既定値を対象とする。
public class RetentionOptionsTests
{
    [Fact]
    public void 既定は無効であり_有効化は明示的なオプトインである()
    {
        // IADR-0059 決定4: 不可逆な DELETE を既定 ON にしない。
        new RetentionOptions().Enabled.Should().BeFalse();
    }

    [Fact]
    public void 既定値は_90_日_24_時間_500_行である()
    {
        var options = new RetentionOptions();

        options.RetentionDays.Should().Be(RetentionPolicy.DefaultRetentionDays);
        options.IntervalHours.Should().Be(24);
        options.BatchSize.Should().Be(500);
    }

    [Fact]
    public void 節名は_Retention_である()
    {
        // 費用統制・発注執行の両 Worker が同じ節を読む（設定の単一情報源）。
        RetentionOptions.SectionName.Should().Be("Retention");
    }

    [Theory]
    [InlineData(24, 24)]
    [InlineData(1, 1)]
    [InlineData(168, 168)]
    // 設定ミス（0/負）でも下限 1 時間。高頻度で回り続けて DB を叩かないようにする。
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    // 上限 1 年。TimeSpan.FromHours(int.MaxValue) はオーバーフローで例外を投げ、BackgroundService の
    // 起動時（ループ外の Interval 参照）で落ちる＝設定ミスでサービスが停止する。
    [InlineData(RetentionOptions.MaximumIntervalHours + 1, RetentionOptions.MaximumIntervalHours)]
    [InlineData(int.MaxValue, RetentionOptions.MaximumIntervalHours)]
    public void 巡回間隔は_1_時間から_1_年にクランプされる(int configured, int expectedHours)
    {
        new RetentionOptions { IntervalHours = configured }.Interval
            .Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(1, 1)]
    [InlineData(10_000, 10_000)]
    // 下限 1: 0 や負値で「1 行も消えないのに巡回だけ回る」状態にしない。
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(int.MinValue, 1)]
    // 上限 10000: 1 巡回のメモリ使用（バッチ削除で行を読み込む）に上限を設ける。
    [InlineData(10_001, 10_000)]
    [InlineData(int.MaxValue, 10_000)]
    public void バッチサイズは_1_から_10000_にクランプされる(int configured, int expected)
    {
        new RetentionOptions { BatchSize = configured }.EffectiveBatchSize.Should().Be(expected);
    }

    [Fact]
    public void 巡回間隔はどんな設定値でも例外にならず常に正である()
    {
        // 非正の間隔は Task.Delay を即時復帰させ巡回が暴走する。オーバーフローは BackgroundService を
        // 起動時に落とす。どんな設定値でも、どちらも起こしてはならない。
        foreach (var hours in new[] { int.MinValue, -1, 0, 1, 24, int.MaxValue })
        {
            var act = () => new RetentionOptions { IntervalHours = hours }.Interval;

            act.Should().NotThrow().Which.Should().BePositive();
        }
    }
}
