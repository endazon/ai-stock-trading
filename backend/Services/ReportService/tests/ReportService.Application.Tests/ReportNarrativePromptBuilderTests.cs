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

    // T-4, FR-06/07, IADR-0117 決定3, #293, 04_workflows/03_reporting-cycle:
    // 上位方針（日報なら当週の週報）の**本文**をプロンプトへ載せ、差異評価を指示する。
    // 計画の業務フローは「AI がドラフト生成＝週報の目標との差異評価＋翌営業日の目標案」と明記しており、
    // 期間キーだけでは差異評価が書けない（本文が要る）。
    [Fact]
    public void 上位方針の期間キーと本文を含め差異評価を指示する()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            ParentPeriodKey = "weekly-2026-W29",
            ParentPolicySummary = "今週は半導体セクターを重点監視し、1 銘柄あたりの建玉を 2 単位までとする",
        });

        prompt.Should().Contain("週報");                        // 日報の上位は週報
        prompt.Should().Contain("weekly-2026-W29");
        prompt.Should().Contain("半導体セクターを重点監視");     // 本文がそのまま入る
        prompt.Should().MatchRegex("差異|達成度");               // 差異評価の指示
    }

    // T-5, IADR-0117 決定3, 03_reporting-cycle「上位方針の欠落」:
    // 上位が未確定なら**その旨を明記**する。捏造せず、参照できなかった事実を散文へ反映させる。
    [Fact]
    public void 上位方針が未確定ならその旨を明記する()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context());  // ParentPolicySummary 未指定

        prompt.Should().MatchRegex("上位方針.*(未確定|参照していません|参照できません)");
    }

    // T-4, IADR-0117 決定3: 月報の上位は「前月の月報」（ParentKind(Monthly) == Monthly）。
    // 自種別の継続案（PolicySummary）と紛れないよう、上位はその呼称で提示する。
    [Fact]
    public void 月報の上位は前月の月報として提示する()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context(ReportKind.Monthly) with
        {
            ParentPeriodKey = "monthly-2026-06",
            ParentPolicySummary = "6 月は現金比率を 30% 以上に保つ",
        });

        prompt.Should().Contain("前月の月報");
        prompt.Should().Contain("monthly-2026-06");
        prompt.Should().Contain("現金比率");
    }

    // T-8, FR-16 回帰: 上位方針を足しても「数値はコード集計が唯一の権威」という制約は崩さない。
    // 上位方針の本文に数値が含まれ得るため、再計算禁止の指示が残っていることを明示的に固定する。
    [Fact]
    public void 上位方針を載せても数値の再計算禁止の指示は残る()
    {
        var prompt = ReportNarrativePromptBuilder.Build(Context() with
        {
            ParentPeriodKey = "weekly-2026-W29",
            ParentPolicySummary = "週次の損失上限は 5000 円とする",
        });

        prompt.Should().MatchRegex("再計算|改変|変更しない");
        prompt.Should().Contain("散文");
    }
}
