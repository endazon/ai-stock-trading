using MarketMonitorService.Domain;
using AwesomeAssertions;
using Xunit;

namespace MarketMonitorService.Tests;

// FR-03, FR-13, SC-02, #423, IADR-0155 決定3 / IADR-0164 決定2:
// 市場監視パラメータ（変動閾値・クールダウン）の値域の**境界**を固定する。
//
// 2026-08-07 の裁定（質問票 第 13 回 Q13）が値域とその理由を明記した。
//   変動閾値   … **0 超 0.50 以下**。0 以下では**あらゆる変動が閾値超過**となり、
//                 イベント駆動サイクルが常時発火して **LLM 費用が暴走する**。
//                 50% 超では 1 日で半値になる変動でしか発火せず FR-03 が事実上無効になる。
//   クールダウン … **0 以上 24 時間以下**。24 時間超では同一銘柄が 1 営業日に 1 度も再判定されない。
//
// **境界そのものが安全性の主張である。** 0 を許すか許さないかで費用の挙動が変わり、
// 0.50 を含むか含まないかで「50% ちょうど」の設定が通るかが変わる。
public class MonitorSettingsBoundsTests
{
    // ------------------------------------------------------------------
    // 変動閾値: 0 は不可 / 0 超は可 / 0.50 は可 / 0.50 超は不可
    // ------------------------------------------------------------------

    // **0 を許してはならない。** 0 では「前回判断時点との差が 0 以上」＝常に閾値超過であり、
    // 監視が統制ではなく常時発火装置になる（LLM 費用の暴走）。
    [Theory]
    [InlineData(0)]
    [InlineData(-0.0001)]
    [InlineData(-1)]
    public void 変動閾値の0以下は受理しない(double ratio)
    {
        MonitorSettingsBounds.ValidateMovementThresholdRatio((decimal)ratio).Should().NotBeNull();
    }

    // 0 を「超えて」いれば、どれほど小さくても値域内である（下限は開区間）。
    [Theory]
    [InlineData(0.0001)]
    [InlineData(0.01)]
    [InlineData(0.03)]
    [InlineData(0.49)]
    public void 変動閾値の0超は受理する(double ratio)
    {
        MonitorSettingsBounds.ValidateMovementThresholdRatio((decimal)ratio).Should().BeNull();
    }

    // **上限 0.50 は「含む」。** 境界を閉区間にするか開区間にするかは計画が
    // 「0.50 以下」と定めており、ここを取り違えると 50% ちょうどの設定が通らなくなる。
    [Fact]
    public void 変動閾値の0_50ちょうどは受理する()
    {
        MonitorSettingsBounds.ValidateMovementThresholdRatio(0.50m).Should().BeNull();
        MonitorSettingsBounds.MaxMovementThresholdRatio.Should().Be(0.50m);
    }

    [Theory]
    [InlineData(0.5001)]
    [InlineData(0.51)]
    [InlineData(1.0)]
    public void 変動閾値の0_50超は受理しない(double ratio)
    {
        MonitorSettingsBounds.ValidateMovementThresholdRatio((decimal)ratio).Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // クールダウン: 0 は可 / 24h は可 / 24h 超は不可 / 負値は不可
    // ------------------------------------------------------------------

    // **0 は許す**（抑制なし）。変動閾値と非対称であることは意図的である——
    // クールダウン 0 は「同じ銘柄をすぐ再判定する」であり、閾値 0 のような常時発火ではない。
    [Fact]
    public void クールダウンの0は受理する()
    {
        MonitorSettingsBounds.ValidateCooldown(TimeSpan.Zero).Should().BeNull();
    }

    [Fact]
    public void クールダウンの24時間ちょうどは受理する()
    {
        MonitorSettingsBounds.ValidateCooldown(TimeSpan.FromHours(24)).Should().BeNull();
        MonitorSettingsBounds.MaxCooldown.Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void クールダウンの24時間超は受理しない()
    {
        MonitorSettingsBounds.ValidateCooldown(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1))
            .Should().NotBeNull();
        MonitorSettingsBounds.ValidateCooldown(TimeSpan.FromHours(48)).Should().NotBeNull();
    }

    [Fact]
    public void クールダウンの負値は受理しない()
    {
        MonitorSettingsBounds.ValidateCooldown(TimeSpan.FromMinutes(-1)).Should().NotBeNull();
    }
}
