using FluentAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06/16, 04_report-templates, IADR-0032: 報告書テンプレートへの組み立て（純関数・日報/週報/月報）を検証する。
// 数値は PnlSummary（コード集計値）から埋まり、散文・方針が挿入されることを確認する。
public class ReportRendererTests
{
    private static PnlSummary Pnl() => new(
        RealizedPnlGross: 2_000m, TotalCost: 100m, TaxWithheld: 380m, RealizedPnlNet: 1_520m,
        UnrealizedPnl: -300m, TradeCount: 3, RealizingTradeCount: 4, WinningTradeCount: 3);

    private static ReportView View(ReportKind kind, string periodLabel, string narrative = "散文テスト", string policy = "方針テスト") => new()
    {
        Kind = kind,
        PeriodKey = $"{kind.ToString().ToLowerInvariant()}-{periodLabel}",
        PeriodLabel = periodLabel,
        Markets = ["JP", "US"],
        AssumptionsVersion = 2,
        BasedOn = "weekly-2026-W28",
        Pnl = Pnl(),
        BuyCount = 2,
        SellCount = 1,
        PolicySummary = policy,
        Narrative = narrative,
    };

    // --- 日報（#86 からの回帰維持） ---

    [Fact]
    public void 日報_フロントマターと当日サマリの数値を定義どおり生成する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-07-10"));

        md.Should().StartWith("---\n");
        md.Should().Contain("report_type: daily");
        md.Should().Contain("period: 2026-07-10");
        md.Should().Contain("assumptions_version: v2");
        md.Should().Contain("markets: [JP, US]");
        md.Should().Contain("# 日報 2026-07-10");
        md.Should().Contain("## 1. 当日サマリ");
        md.Should().Contain("実現損益（税引後・費用込み） | +1,520 円");
        md.Should().Contain("評価損益（税引前・参考） | -300 円");
        md.Should().Contain("取引回数（買/売/決済） | 2 / 1 / 4");
        md.Should().Contain("源泉徴収税額 | +380 円");
        md.Should().Contain("## 2. 市況・振り返り");
        md.Should().Contain("## 3. 翌営業日の方針");
        // 日報は勝率行を持たない。
        md.Should().NotContain("勝率");
    }

    // --- 週報 ---

    [Fact]
    public void 週報_週間サマリと勝率と翌週方針を生成する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Weekly, "2026-W28"));

        md.Should().Contain("report_type: weekly");
        md.Should().Contain("period: 2026-W28");
        md.Should().Contain("# 週報 2026-W28");
        md.Should().Contain("## 1. 週間サマリ");
        md.Should().Contain("週間実現損益（税引後・費用込み） | +1,520 円");
        md.Should().Contain("勝率（勝ち/決済） | 3/4（75%）"); // WinningTradeCount/RealizingTradeCount
        md.Should().Contain("## 2. 振り返りと評価");
        md.Should().Contain("## 3. 翌週の方針");
    }

    // --- 月報 ---

    [Fact]
    public void 月報_月間サマリと翌月方針を生成する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-07"));

        md.Should().Contain("report_type: monthly");
        md.Should().Contain("period: 2026-07");
        md.Should().Contain("# 月報 2026-07");
        md.Should().Contain("## 1. 月間サマリ");
        md.Should().Contain("月間実現損益（税引後・費用込み） | +1,520 円");
        md.Should().Contain("勝率（勝ち/決済） | 3/4（75%）");
        md.Should().Contain("## 3. 翌月の方針・投資方針");
    }

    [Fact]
    public void 決済ゼロの勝率はハイフン表記()
    {
        var view = View(ReportKind.Weekly, "2026-W28") with
        {
            Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
        };

        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().Contain("勝率（勝ち/決済） | 0/0（-）");
    }

    [Fact]
    public void 散文と方針が空でも見出しと既定文で生成される()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-07-10", narrative: "", policy: ""));

        md.Should().Contain("（散文ドラフトなし）");
        md.Should().Contain("（方針未設定）");
    }
}
