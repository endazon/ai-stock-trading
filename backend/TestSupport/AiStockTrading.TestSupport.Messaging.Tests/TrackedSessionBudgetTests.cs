using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.TestSupport.Messaging.Tests;

/// <summary>
/// NFR, #357, IADR-0168: 予算の解決規則を固定する。
/// </summary>
public class TrackedSessionBudgetTests
{
    // 既定は Wolverine の既定（5 秒）より**明確に長い**。ここが縮むと #357 の flake が戻る。
    // 「30 秒であること」ではなく「5 秒より十分長いこと」を表明する——具体値の変更で落とすべき
    // テストではなく、**予算の性格（ハングの検知であって性能の表明ではない）**を守るテストである。
    [Fact]
    public void 既定の予算はWolverineの既定5秒より十分に長い()
    {
        TrackedSessionBudget.Default.Should().BeGreaterThan(
            TimeSpan.FromSeconds(15),
            "並列実行で CPU が飽和したときのスケジューリング遅延を、ハングと取り違えないため");
    }

    [Theory]
    [InlineData("10", 10d)]
    [InlineData("0.5", 0.5d)]
    [InlineData("120", 120d)]
    public void 環境変数が正の数なら上書きできる(string raw, double expectedSeconds)
    {
        TrackedSessionBudget.Resolve(raw).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    // **読めない値では既定へ倒す。** 倒す先を既定にするのは、設定ミスでタイムアウトが 0 になると
    // 全テストが即座に落ちるためである。環境変数の誤りでテストが壊れるより、上書きが効かないほうが
    // 失敗モードとして軽い（fail-safe の向き）。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("10s")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void 読めない値や非正の値では既定へ倒す(string? raw)
    {
        TrackedSessionBudget.Resolve(raw).Should().Be(
            TrackedSessionBudget.Default,
            "設定ミスで予算が 0 になると全テストが即座に落ちる。上書きが効かないほうが軽い");
    }

    // 小数点の解釈をロケールに委ねない（`0.5` がロケール次第で 5 と読まれると予算が 10 倍になる）。
    [Fact]
    public void 小数点はロケールに依存せず不変文化で解釈する()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            TrackedSessionBudget.Resolve("0.5").Should().Be(TimeSpan.FromSeconds(0.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
