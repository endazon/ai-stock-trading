using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, ADR-0008: LLM 学習カットオフ後データ／銘柄匿名化（検証条件①）。LLM 汚染の排除を検証する。
public class DataCutoffPolicyTests
{
    private static readonly DateOnly Cutoff = new(2024, 12, 31);

    private static PriceBar Bar(int year, int month, int day) =>
        new("AAA", Market.UnitedStates, new DateOnly(year, month, day), 10m, 10m, 10m, 10m, 100);

    [Fact]
    public void 全バーがカットオフ後なら合格()
    {
        var bars = new[] { Bar(2025, 1, 1), Bar(2025, 6, 1) };
        DataCutoffPolicy.IsAllAfterCutoff(bars, Cutoff).Should().BeTrue();
    }

    [Fact]
    public void カットオフ当日以前のバーがあれば不合格()
    {
        var bars = new[] { Bar(2025, 1, 1), Bar(2024, 12, 31) }; // 12/31 はカットオフ当日（後ではない）
        DataCutoffPolicy.IsAllAfterCutoff(bars, Cutoff).Should().BeFalse();
    }

    [Fact]
    public void 違反バーを列挙できる()
    {
        var bars = new[] { Bar(2025, 1, 1), Bar(2024, 6, 1), Bar(2023, 1, 1) };
        DataCutoffPolicy.Violations(bars, Cutoff).Should().HaveCount(2);
    }

    [Fact]
    public void 空のバー列は合格_違反なし()
    {
        DataCutoffPolicy.IsAllAfterCutoff([], Cutoff).Should().BeTrue();
    }
}
