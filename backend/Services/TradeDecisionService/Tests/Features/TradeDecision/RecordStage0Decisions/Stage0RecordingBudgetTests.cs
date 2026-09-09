using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AwesomeAssertions;
using TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;
using Xunit;

namespace TradeDecisionService.Tests.Features.TradeDecision.RecordStage0Decisions;

// FR-15, NFR（費用）, ADR-0033 決定5, #632, IADR-0318: Stage 0 記録の費用見積り（境界値テーブル）。
//
// 計画の式: 銘柄数 × 対象営業日数 × 1 日あたり判断回数 × 多数決回数 → 呼び出し回数、
// 呼び出し回数 × 1 判断あたりトークン量 × 単価表 → 円。
public class Stage0RecordingBudgetTests
{
    // 円/1k トークン。入力 0.45 / 出力 2.25 は表の形を確かめるための任意値であり、計画の確定値ではない。
    private static readonly LlmPrice Price = new(0.45m, 2.25m);

    [Theory]
    // 銘柄 / 営業日 / 1日回数 / 多数決 / 入力Tk / 出力Tk / 期待呼び出し回数 / 期待円
    [InlineData(1, 1, 1, 1, 1000, 200, 1, 0.9)]        // 0.45 + 0.45
    [InlineData(2, 10, 1, 3, 1000, 200, 60, 54.0)]     // 60 呼び出し
    [InlineData(1, 1, 1, 3, 1000, 200, 3, 2.7)]        // 多数決回数に比例する
    [InlineData(1, 1, 2, 1, 1000, 200, 2, 1.8)]        // 1 日あたり判断回数に比例する
    [InlineData(0, 10, 1, 3, 1000, 200, 0, 0.0)]       // 銘柄 0 なら 0
    [InlineData(1, 0, 1, 3, 1000, 200, 0, 0.0)]        // 営業日 0 なら 0
    [InlineData(1, 1, 1, 0, 1000, 200, 0, 0.0)]        // 多数決 0 なら 0
    [InlineData(1, 1, 1, 1, 0, 0, 1, 0.0)]             // トークン量未設定は 0 円（＝承認値と一致せず実行されない）
    [InlineData(-3, -3, -3, -3, -3, -3, 0, 0.0)]       // 負値は 0 として扱う
    public void 見積りは計画の式どおりに算出される(
        int symbols, int days, int perDay, int votes, int inputTokens, int outputTokens,
        long expectedCalls, double expectedJpy)
    {
        var estimate = Stage0RecordingBudget.Estimate(
            new Stage0RecordingEstimateInput(symbols, days, perDay, votes, inputTokens, outputTokens), Price);

        estimate.CallCount.Should().Be(expectedCalls);
        estimate.TotalJpy.Should().BeApproximately((decimal)expectedJpy, 0.0001m);
    }

    [Fact]
    public void 見積りはトークン量の内訳を持つ()
    {
        var estimate = Stage0RecordingBudget.Estimate(
            new Stage0RecordingEstimateInput(2, 5, 1, 3, 1_500, 300), Price);

        estimate.CallCount.Should().Be(30);
        estimate.InputTokens.Should().Be(45_000);
        estimate.OutputTokens.Should().Be(9_000);
    }

    // 🔴 見積りの営業日数は**平日の数**である。休場日は差し引かない（＝過大側に寄る）。
    // 予算の見積りとして安全な向きであり、実績は必ず見積り以下になる。
    [Theory]
    [InlineData("2026-06-01", "2026-06-05", 5)]   // 月〜金
    [InlineData("2026-06-01", "2026-06-07", 5)]   // 週末は数えない
    [InlineData("2026-06-06", "2026-06-07", 0)]   // 土日のみ
    [InlineData("2026-06-01", "2026-06-01", 1)]   // 単日（平日）
    [InlineData("2026-06-30", "2026-06-01", 0)]   // 逆順は 0
    public void 平日の数を数える(string from, string to, int expected) =>
        Stage0RecordingBudget.WeekdayCount(DateOnly.Parse(from), DateOnly.Parse(to)).Should().Be(expected);
}
