using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Application.Tests;

// T-1, FR-06/11, IADR-0120 決定1, #291: 報告書は方針階層（月報→週報→日報→取引・04_workflows/03_reporting-cycle）を
// なす方針書であり、上位ほど難度が高い。種別ごとに purpose を分けることで基盤（platform LlmGateway）の
// Llm:Routing:PurposeModels が種別ごとのモデルを解決できる。
//
// 写像を純関数として Application に置くのは、HTTP を立てずに 3 種別の期待値を固定するためである。
// 値は基盤の PurposeModels のキーと**文字列として一致していなければならない**（不一致だと未知 purpose として
// DefaultModel へ黙って落ち、割当が無音で失効する）。本テストはその契約値そのものを固定する。
public class ReportNarrativePurposeTests
{
    [Theory]
    [InlineData(ReportKind.Daily, "report-daily")]
    [InlineData(ReportKind.Weekly, "report-weekly")]
    [InlineData(ReportKind.Monthly, "report-monthly")]
    public void 種別ごとに別のpurposeを返す(ReportKind kind, string expected)
    {
        ReportNarrativePurpose.For(kind).Should().Be(expected);
    }

    // 3 種別が同一 purpose へ潰れていないこと自体が方針階層の反映である（潰れると単一モデルに戻る）。
    [Fact]
    public void 三種別のpurposeは互いに異なる()
    {
        var purposes = new[] { ReportKind.Daily, ReportKind.Weekly, ReportKind.Monthly }
            .Select(ReportNarrativePurpose.For)
            .ToList();

        purposes.Should().OnlyHaveUniqueItems();
    }
}
