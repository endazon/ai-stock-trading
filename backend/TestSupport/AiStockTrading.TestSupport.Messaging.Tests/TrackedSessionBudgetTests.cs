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

    // NFR, #922: 短縮入口（`IServiceProvider.ExecuteAndWaitAsync`）へ渡すミリ秒。既定の予算がそのまま届くこと。
    [Fact]
    public void 既定の予算はミリ秒へそのまま直る()
    {
        TrackedSessionBudget.ToTimeoutMilliseconds(TrackedSessionBudget.Default).Should().Be(30_000);
    }

    // NFR, #922: 予算を縮める向きに倒さない（切り捨てで 0 にしない・キャストで負へ桁あふれさせない）。
    [Theory]
    [InlineData(0.5d, 1)]
    [InlineData(1.2d, 2)]
    [InlineData(0d, 1)]
    public void 端数は切り上げ下限は1ミリ秒(double milliseconds, int expected)
    {
        TrackedSessionBudget.ToTimeoutMilliseconds(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);
    }

    [Fact]
    public void intに収まらない予算はintの最大値で頭打ちにする()
    {
        TrackedSessionBudget.ToTimeoutMilliseconds(TimeSpan.FromDays(365)).Should().Be(int.MaxValue);
    }
}
