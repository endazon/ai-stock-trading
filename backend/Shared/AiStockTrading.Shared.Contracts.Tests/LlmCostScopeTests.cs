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

    // 🔴 **否定形**（FR-15, ADR-0033 決定5, #632, IADR-0318 決定4）: Stage 0 の記録で発生した LLM 費用は
    // 月次上限（15,000 円・取引判断サイクル対象）へ積まない。積むと、検証の実行が本番取引の抑制
    // （80% で間隔延長・100% で停止）を引き起こす —— 計画 §6.1 が報告書生成について既に避けている連鎖と同型である。
    [Theory]
    [InlineData(LlmPurposes.Stage0Recording)]
    [InlineData("STAGE0-RECORDING")] // 大小は無視する
    public void Stage0記録の費用は上限の対象外である(string purpose) =>
        LlmCostScope.IsGoverned(purpose).Should().BeFalse();

    // FR-15, ADR-0033 決定5: Stage 0 の記録は取引判断系にも報告書にも属さない**第 3 の区分**である
    // （どちらかに寄せると、抑制動作か月報の内訳のどちらかが実態と食い違う）。
    // #750: その第 3 の区分は `IsStage0Recording` が名指しで判定する（月報 §7 の対比列がこれで分別する）。
    [Fact]
    public void Stage0記録は取引判断系でも報告書でもない独立の区分である()
    {
        LlmPurposes.IsTradeDecision(LlmPurposes.Stage0Recording).Should().BeFalse();
        LlmPurposes.IsReport(LlmPurposes.Stage0Recording).Should().BeFalse();
        LlmPurposes.IsStage0Recording(LlmPurposes.Stage0Recording).Should().BeTrue();
    }

    // 🔴 **否定形**（FR-15, ADR-0037 決定3, #750）: 他の用途を Stage 0 記録と誤って数えない。
    // 誤ると月報 §7 の対比の分子が膨らみ、**起きていない超過を報告する**。
    [Theory]
    [InlineData(LlmPurposes.TradeDecision)]
    [InlineData(LlmPurposes.TradeDecisionScreening)]
    [InlineData(LlmPurposes.ReportMonthly)]
    [InlineData("information-collection")]
    [InlineData(null)]
    [InlineData("")]
    public void Stage0記録の判定は他の用途を拾わない(string? purpose) =>
        LlmPurposes.IsStage0Recording(purpose).Should().BeFalse();

    // 大小は無視する（他の判定と同じ規律。台帳の記録が大文字で残っても取りこぼさない）。
    [Fact]
    public void Stage0記録の判定は大小を無視する() =>
        LlmPurposes.IsStage0Recording("STAGE0-RECORDING").Should().BeTrue();

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
