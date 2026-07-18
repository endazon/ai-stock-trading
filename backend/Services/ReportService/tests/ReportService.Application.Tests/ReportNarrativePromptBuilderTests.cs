using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Report.Application.Services;
using AiStockTrading.Report.Domain;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Application.Tests;

// FR-06/16, IADR-0071 決定1: 散文ドラフトの LLM プロンプトは純関数で決定的に構築する。数値は参考として載せるが
// 「再計算・改変しない（数値はコード集計が権威）」を明示する（FR-16）。プロンプト構築は Application に置き実 LLM 実装から分離する。
public class ReportNarrativePromptBuilderTests
{
    private static ReportNarrativeContext Context(ReportKind kind = ReportKind.Daily) => new(
        Kind: kind,
        PeriodKey: "daily-2026-07-18",
        PeriodLabel: "2026-07-18",
        Markets: ["JP", "US"],
        Pnl: new PnlSummary(
            RealizedPnlGross: 12000m, TotalCost: 500m, TaxWithheld: 2000m, RealizedPnlNet: 9500m,
            UnrealizedPnl: -300m, TradeCount: 4, RealizingTradeCount: 2, WinningTradeCount: 1),
        PolicySummary: "翌営業日は押し目買いを継続");

    [Fact]
    public void 期間種別と対象期間を含める()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context(ReportKind.Weekly));

        prompt.Should().Contain("週報");
        prompt.Should().Contain("daily-2026-07-18");
        prompt.Should().Contain("2026-07-18");
    }

    [Fact]
    public void 集計済みの数値を参考として提示する()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context());

        // 実現損益(税引後)・費用・評価損益など集計値がプロンプトに現れる（LLM への提示）。
        prompt.Should().Contain("9500");
        prompt.Should().Contain("12000");
        prompt.Should().Contain("-300");
    }

    [Fact]
    public void 数値を再計算改変しない指示を含める()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context());

        // FR-16: 数値は LLM に計算させない。散文のみを求め、数値の再計算・改変を禁じる指示が入る。
        prompt.Should().Contain("散文");
        prompt.Should().MatchRegex("再計算|改変|変更しない");
    }

    [Fact]
    public void 市場と方針要旨を含める()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context());

        prompt.Should().Contain("JP");
        prompt.Should().Contain("US");
        prompt.Should().Contain("押し目買い");
    }

    [Fact]
    public void 決定的_同一入力で同一出力()
    {
        ReportNarrativePromptBuilder.Build(Context()).Should().Be(ReportNarrativePromptBuilder.Build(Context()));
    }
}
