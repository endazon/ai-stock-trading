using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-01, FR-02, FR-10, ADR-0020 決定2/決定3, #337, IADR-0249:
// 情報収集の縮退（BlocksNewEntries）による新規建て停止の判定コア検証。
//
// 統制系のため 3 点セット（docs/tests/README.md §2）で構成する。
//   1. 境界値テーブル — 縮退 × 建玉効果（Open/Close）× 売買方向の組み合わせ
//   2. プロパティベース — 他統制との全組み合わせでも手仕舞いは本理由で止まらない（不変条件）
//   3. 否定形 — 縮退が無ければ理由が立たない／縮退中でも Close は承認される
public class InformationDegradationEvaluationTests
{
    private static OrderIntent Intent(PositionEffect effect, TradeSide side = TradeSide.Buy) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.InternalPaper, 10, 2_000m, effect);

    private static PortfolioSnapshot Snapshot(
        bool informationDegraded = false, bool killSwitch = false, bool paused = false) =>
        new()
        {
            Capital = 100_000m,
            SymbolsTradedToday = new HashSet<(string, Market)>(),
            KillSwitchEngaged = killSwitch,
            TradingPaused = paused,
            InformationDegradedBlocksNewEntries = informationDegraded,
        };

    private static RiskManagementSettings Settings() => TradingDefaults.CreateSettings();

    // 1. 境界値テーブル: 縮退 × 建玉効果。
    [Theory]
    [InlineData(false, PositionEffect.Open, false)]  // 縮退なし → 立たない（否定形の対）
    [InlineData(true, PositionEffect.Open, true)]    // 縮退中の新規建て → 拒否
    [InlineData(false, PositionEffect.Close, false)]
    [InlineData(true, PositionEffect.Close, false)]  // 縮退中でも手仕舞いは止めない
    public void 縮退中は新規建てのみ拒否理由が立つ(bool degraded, PositionEffect effect, bool expectReason)
    {
        var side = effect == PositionEffect.Close ? TradeSide.Sell : TradeSide.Buy;
        var result = RiskEvaluator.Evaluate(Intent(effect, side), Settings(), Snapshot(informationDegraded: degraded));

        result.Reasons.Contains(RejectionReason.InformationSourceDegraded).Should().Be(expectReason);
    }

    [Fact]
    public void 縮退中の新規建ては拒否され承認されない()
    {
        var result = RiskEvaluator.Evaluate(Intent(PositionEffect.Open), Settings(), Snapshot(informationDegraded: true));

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().Contain(RejectionReason.InformationSourceDegraded);
    }

    [Fact]
    public void 縮退中でも手仕舞いは承認される_否定形()
    {
        // ADR-0020 決定2/決定3・ADR-0009 の不変条件: 限定縮退は決済を止めない。
        // 「止められない」より「閉じられない」ほうが危険（IADR-0197 と同じ形を作らない）。
        var result = RiskEvaluator.Evaluate(
            Intent(PositionEffect.Close, TradeSide.Sell), Settings(), Snapshot(informationDegraded: true));

        result.IsApproved.Should().BeTrue();
        result.Reasons.Should().BeEmpty();
    }

    // 2. プロパティベース: 3 統制との全組み合わせ（2^3）でも、手仕舞いが本理由で止まることはない。
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 他統制との任意の組み合わせでも手仕舞いは縮退理由で止まらない(bool killSwitch, bool paused)
    {
        foreach (var degraded in new[] { false, true })
        {
            var result = RiskEvaluator.Evaluate(
                Intent(PositionEffect.Close, TradeSide.Sell), Settings(),
                Snapshot(informationDegraded: degraded, killSwitch: killSwitch, paused: paused));

            result.Reasons.Should().NotContain(
                RejectionReason.InformationSourceDegraded,
                "限定縮退は新規建てのみを止める（手仕舞い・損切りは止めない）");
            // 対の肯定形: 同じ状態で新規建ては縮退時に必ず止まる。
            var entry = RiskEvaluator.Evaluate(
                Intent(PositionEffect.Open), Settings(),
                Snapshot(informationDegraded: degraded, killSwitch: killSwitch, paused: paused));
            entry.Reasons.Contains(RejectionReason.InformationSourceDegraded).Should().Be(degraded);
        }
    }

    [Fact]
    public void 縮退はクラスBであり統制違反に計上しない()
    {
        // 市況由来の事象をクラス C（AI が禁止事項を犯そうとした件数）へ混ぜると段階昇格ゲートが壊れる。
        RejectionReasonClassification.ClassOf(RejectionReason.InformationSourceDegraded)
            .Should().Be(RejectionReasonClass.B);
        RejectionReasonClassification.CountsAsControlViolation(RejectionReason.InformationSourceDegraded)
            .Should().BeFalse();
    }
}
