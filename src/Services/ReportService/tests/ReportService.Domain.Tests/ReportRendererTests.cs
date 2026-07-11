using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06/16, 04_report-templates, IADR-0032: 日報テンプレートへの組み立て（純関数）を検証する。
// 数値は PnlSummary（コード集計値）から埋まり、散文・方針が挿入されることを確認する。
public class ReportRendererTests
{
    private static PnlSummary Pnl() => new(
        RealizedPnlGross: 2_000m, TotalCost: 100m, TaxWithheld: 380m, RealizedPnlNet: 1_520m,
        UnrealizedPnl: -300m, TradeCount: 3, RealizingTradeCount: 1);

    private static DailyReportView View(string narrative = "米国株は堅調。押し目を拾えた。", string policy = "翌営業日は押し目買い継続") => new()
    {
        PeriodKey = "daily-2026-07-10",
        Date = new DateOnly(2026, 7, 10),
        Markets = ["JP", "US"],
        AssumptionsVersion = 2,
        BasedOn = "weekly-2026-W28",
        Pnl = Pnl(),
        BuyCount = 2,
        SellCount = 1,
        PolicySummary = policy,
        Narrative = narrative,
    };

    [Fact]
    public void フロントマターに種別_期間_前提バージョン_上位方針を含む()
    {
        var md = ReportRenderer.RenderDailyMarkdown(View());

        md.Should().StartWith("---\n");
        md.Should().Contain("report_type: daily");
        md.Should().Contain("period: 2026-07-10");
        md.Should().Contain("assumptions_version: v2");
        md.Should().Contain("based_on: weekly-2026-W28");
        md.Should().Contain("markets: [JP, US]");
        md.Should().Contain("status: draft"); // 未確定
    }

    [Fact]
    public void 当日サマリ表にコード集計の数値が定義どおり反映される()
    {
        var md = ReportRenderer.RenderDailyMarkdown(View());

        md.Should().Contain("## 1. 当日サマリ");
        md.Should().Contain("実現損益（税引後・費用込み） | +1,520 円"); // RealizedPnlNet
        md.Should().Contain("評価損益（税引前・参考） | -300 円");        // UnrealizedPnl（符号）
        md.Should().Contain("取引回数（買/売/決済） | 2 / 1 / 1");        // Buy/Sell/Realizing
        md.Should().Contain("費用合計（手数料・諸費用・為替） | +100 円");
        md.Should().Contain("源泉徴収税額 | +380 円");
    }

    [Fact]
    public void 散文ドラフトと翌営業日方針が本文に挿入される()
    {
        var md = ReportRenderer.RenderDailyMarkdown(View(narrative: "特記事項テスト散文", policy: "重点は半導体"));

        md.Should().Contain("## 2. 市況・振り返り");
        md.Should().Contain("特記事項テスト散文");
        md.Should().Contain("## 3. 翌営業日の方針");
        md.Should().Contain("重点は半導体");
    }

    [Fact]
    public void 散文と方針が空でも見出しと既定文で生成される()
    {
        var md = ReportRenderer.RenderDailyMarkdown(View(narrative: "", policy: ""));

        md.Should().Contain("（散文ドラフトなし）");
        md.Should().Contain("（方針未設定）");
    }
}
