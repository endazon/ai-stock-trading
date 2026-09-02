using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests.Fx;

// FR-06, FR-16, FR-10, #611, ADR-0022 決定5, IADR-0285 決定1・決定2: 為替レート源の読みから「1 USD あたりの円」を導く規則。
// 認識時（リスク管理の承認記録）と期末（報告書）の両方が通る 1 箇所であり、ここが崩れると両方が同時にずれる。
public class FxBaseToDisplayRateTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 8, 12, 0, 0, 0, TimeSpan.FromHours(9));

    private static FxRateReading Reading(decimal jpyPerUsd, FxRateFreshness freshness = FxRateFreshness.Fresh) =>
        new(new FxRate(Currency.Jpy, Currency.Usd, 1m / jpyPerUsd, AsOf), freshness);

    // 源は「1 USD あたりの円」の**逆数**を返す（IADR-0152 決定2）。ここで逆数を戻し、観測値そのものに復元する。
    [Theory]
    [InlineData(150)]
    [InlineData(159.38)]
    [InlineData(147.1234)]
    public void 読みの逆数を観測値そのものへ復元する(decimal jpyPerUsd)
    {
        FxBaseToDisplayRate.FromReading(Reading(jpyPerUsd)).Should().Be(jpyPerUsd);
    }

    // 🔴 否定形: 読みが無い（源が無い・取得不可）＝未記録／未供給。推定しない。
    [Fact]
    public void 読みが無ければnull()
    {
        FxBaseToDisplayRate.FromReading(null).Should().BeNull();
    }

    // 🔴 否定形: 鮮度切れ（30 日超）は採らない——統制が「新規建てに使えない」と判定した観測を税務に効く数値の根にしない。
    [Fact]
    public void 鮮度切れの読みは採らない()
    {
        FxBaseToDisplayRate.FromReading(Reading(150m, FxRateFreshness.Expired)).Should().BeNull();
    }

    // 対の肯定形: 警告域（5〜30 日）は取引側と同じく採る（計画 §5「直近レートで続行」）。判定なし（Unknown）も採る。
    [Theory]
    [InlineData(FxRateFreshness.Fresh)]
    [InlineData(FxRateFreshness.Warning)]
    [InlineData(FxRateFreshness.Unknown)]
    public void 鮮度切れでなければ採る(FxRateFreshness freshness)
    {
        FxBaseToDisplayRate.FromReading(Reading(150m, freshness)).Should().Be(150m);
    }

    // 契約を破る実装（0 以下）が混ざっても除算例外で承認記録・報告書生成を落とさない。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 非正の観測はnull(decimal jpyToUsd)
    {
        var reading = new FxRateReading(new FxRate(Currency.Jpy, Currency.Usd, jpyToUsd, AsOf), FxRateFreshness.Fresh);

        FxBaseToDisplayRate.FromReading(reading).Should().BeNull();
    }
}
