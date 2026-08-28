using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218:
// 月次 LLM 費用上限の**対象範囲**の判別（境界値テーブル ＋ 否定形）。
//
// 🔴 計画の明文: 「月次 LLM 費用上限 15,000 円の対象は**取引判断サイクルのみ**である。
// 報告書生成・情報収集の LLM 費用は上限の対象外とし、抑制動作も行わず、月報に実績を記載する。」
// 混ぜると「100% 到達で報告書生成が止まる→日報が確定しない→翌営業日の取引が止まる」連鎖が生じる。
public class LlmCostScopeTests
{
    [Theory]
    [InlineData(LlmPurposes.TradeDecision)]
    [InlineData(LlmPurposes.TradeDecisionScreening)]
    [InlineData("TRADE-DECISION")] // 大小は無視する
    public void 取引判断サイクルの費用は上限の対象である(string purpose) =>
        LlmCostScope.IsGoverned(purpose).Should().BeTrue();

    // 🔴 **否定形**（#347 の受け入れ基準）: 報告書生成・情報収集の費用は上限カウンタに積まない。
    [Theory]
    [InlineData(LlmPurposes.ReportMonthly)]
    [InlineData(LlmPurposes.ReportWeekly)]
    [InlineData(LlmPurposes.ReportDaily)]
    [InlineData("REPORT-MONTHLY")]
    [InlineData("information-collection")] // 情報収集（計画 §6.1 の表で対象外）
    [InlineData("rag-answer")]             // 基盤側の用途
    public void 報告書生成と情報収集の費用は上限の対象外である(string purpose) =>
        LlmCostScope.IsGoverned(purpose).Should().BeFalse();

    // 🔴 用途不明は**上限側へ倒す**。費用統制の危険側は過小計上であり、対象外へ倒すと上限が構造的に効かなくなる
    // （IADR-0122 決定3 と同じ判断）。用途を持たない LlmCostIncurred は取引判断サービスの従来の形でもある。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 用途不明の費用は上限側へ倒す_過小計上を作らない(string? purpose) =>
        LlmCostScope.IsGoverned(purpose).Should().BeTrue();

    // プロパティベース: 割当表に載る報告書用途は 1 つ残らず対象外である
    // （用途を足したときに片方だけ直す事故を防ぐ）。
    [Fact]
    public void 割当表の報告書用途はすべて対象外である()
    {
        var reportPurposes = LlmAssignments.All
            .Select(a => a.Purpose)
            .Where(LlmPurposes.IsReport)
            .ToArray();

        reportPurposes.Should().HaveCount(3);
        reportPurposes.Should().OnlyContain(p => !LlmCostScope.IsGoverned(p));
    }

    // 同じくプロパティベース: 割当表に載る取引判断系はすべて対象である。
    [Fact]
    public void 割当表の取引判断用途はすべて対象である()
    {
        var tradePurposes = LlmAssignments.All
            .Select(a => a.Purpose)
            .Where(LlmPurposes.IsTradeDecision)
            .ToArray();

        tradePurposes.Should().HaveCount(2);
        tradePurposes.Should().OnlyContain(p => LlmCostScope.IsGoverned(p));
    }
}
