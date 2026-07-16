using AiStockTrading.Shared.Contracts.Operations;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// NFR（運用）, #137, IADR-0059: 重複排除ストアのパージ境界を決める純関数。
// 本ポリシーが「進行中・再配信猶予内の行を絶対に対象にしない」ことの単一情報源であり、
// 設定ミス（0/負値）でも下限 7 日でクランプされることが安全性の要（IADR-0059 決定3）。
public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 既定の保持期間は_90_日である()
    {
        // IADR-0059 決定2: 自動再配送（約42秒）・手動再投入（数日）の桁違いに外側。
        RetentionPolicy.DefaultRetentionDays.Should().Be(90);
    }

    [Fact]
    public void 下限の保持期間は_7_日である()
    {
        RetentionPolicy.MinimumRetentionDays.Should().Be(7);
    }

    [Fact]
    public void 既定値では_90_日より古い行だけが_cutoff_の外側になる()
    {
        var cutoff = RetentionPolicy.CutoffFor(Now, RetentionPolicy.DefaultRetentionDays);

        cutoff.Should().Be(Now.AddDays(-90));
    }

    [Theory]
    // 設定ミス（0/負/下限未満）でも 7 日でクランプする＝直近の行は決して消えない（IADR-0059 決定3）。
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-9999)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(int.MinValue)]
    public void 下限未満の保持期間は_7_日にクランプされる(int configured)
    {
        var cutoff = RetentionPolicy.CutoffFor(Now, configured);

        cutoff.Should().Be(Now.AddDays(-RetentionPolicy.MinimumRetentionDays));
        RetentionPolicy.EffectiveRetentionDays(configured).Should().Be(RetentionPolicy.MinimumRetentionDays);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public void 下限以上の保持期間はそのまま用いる(int configured)
    {
        RetentionPolicy.EffectiveRetentionDays(configured).Should().Be(configured);
        RetentionPolicy.CutoffFor(Now, configured).Should().Be(Now.AddDays(-configured));
    }

    [Fact]
    public void cutoff_は常に現在時刻より過去である()
    {
        // 「未来の cutoff」＝全行パージを意味する。どんな設定値でも起こしてはならない。
        foreach (var days in new[] { int.MinValue, -1, 0, 7, 90, int.MaxValue })
        {
            RetentionPolicy.CutoffFor(Now, days).Should().BeBefore(Now);
        }
    }

    [Fact]
    public void 極端に大きい保持期間でも例外にならず_過去に収まる()
    {
        // int.MaxValue 日は DateTimeOffset の範囲外になり得る。オーバーフローで落とさず下限側へ倒す。
        var cutoff = RetentionPolicy.CutoffFor(Now, int.MaxValue);

        cutoff.Should().BeBefore(Now);
    }
}
