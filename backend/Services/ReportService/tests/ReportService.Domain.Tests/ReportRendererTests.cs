using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
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
        md.Should().Contain("勝率（勝ち取引/全決済取引） | 75%（3/4）"); // 04_report-templates の <n%（n/n）> 形式
        md.Should().Contain("週次目標に対する達成 | （データ連携後）"); // 目標データ連携は後続
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
        // 04_report-templates の月間サマリ行構成。データ依存行は後続連携で埋める（形式は保つ）。
        md.Should().Contain("総資産（月初 → 月末） | （データ連携後）");
        md.Should().Contain("年初来累計損益 | （データ連携後）");
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

        md.Should().Contain("勝率（勝ち取引/全決済取引） | -（0/0）");
    }

    [Fact]
    public void 散文と方針が空でも見出しと既定文で生成される()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-07-10", narrative: "", policy: ""));

        md.Should().Contain("（散文ドラフトなし）");
        md.Should().Contain("（方針未設定）");
    }

    // --- 維持率割れによる自動縮小（#330・記録先 3/4: 日報・月報） ---

    private static MaintenanceMarginReductionExecuted Reduction(
        DateTimeOffset executedAt, decimal? ratioAfter = 0.4504m) => new(
        Guid.NewGuid(), RatioBefore: 0.40m, Threshold: 0.40m, RecoveryTarget: 0.45m, RatioAfter: ratioAfter,
        [new MaintenanceMarginReductionItem(
            "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.ShortSell, 112, 100m, 3_360m)],
        executedAt);

    // T-10-201: 日報は**発動の有無・決済した建玉・決済前後の維持率**を表 7 列で載せる
    //（04_report-templates 日報 §4・FR-10・UC-06）。
    [Fact]
    public void 日報は維持率割れの自動縮小を7列の表で記載する()
    {
        var view = View(ReportKind.Daily, "2026-08-04") with
        {
            MarginReductions =
            [
                Reduction(new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero)),
                Reduction(new DateTimeOffset(2026, 8, 4, 12, 5, 0, TimeSpan.Zero)),
            ],
        };

        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().Contain("### 維持率割れによる自動縮小（当日）");
        md.Should().Contain("**発動の有無**: あり — 2 回");
        md.Should().Contain("| # | 時刻 | 決済前の維持率 | 閾値 | 回復目標（閾値+5pt） | 決済した建玉（銘柄・方向・数量・必要証拠金） | 決済後の維持率 |");
        md.Should().Contain("| 1 | 12:05 |", "発動は時刻の昇順で並べる");
        md.Should().Contain("40.0%").And.Contain("45.0%").And.Contain("45.0%");
        md.Should().Contain("AAPL / ショート / 112 株 / 3,360.00 USD");
    }

    // T-10-202: 発動が無い日も「なし」と**明記する**（計画: 空欄と「なし」を区別する）。
    [Fact]
    public void 日報は発動が無い日もなしと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Daily, "2026-08-04") with { MarginReductions = [] });

        md.Should().Contain("**発動の有無**: なし");
    }

    // T-10-203（否定形）: **照会できなかったものを「なし」と書かない**（発動を隠したのと同じ結果になるため）。
    [Fact]
    public void 記録を照会できないときはなしと書かず要確認と記す()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Daily, "2026-08-04") with { MarginReductions = null });

        md.Should().Contain("記録を照会できませんでした（要確認）");
        md.Should().NotContain("**発動の有無**: なし");
    }

    // T-10-204: 月報は**当月の発動回数**のみを記載する（明細は日報に委ねる。04_report-templates 月報 §6）。
    // 発動が無い月も「0 件」と明記する。
    [Fact]
    public void 月報は当月の発動回数を記載し発動が無い月も0件と明記する()
    {
        var withEvents = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, "2026-08") with
        {
            MarginReductions = [Reduction(new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero))],
        });
        var empty = ReportRenderer.RenderMarkdown(
            View(ReportKind.Monthly, "2026-08") with { MarginReductions = [] });

        withEvents.Should().Contain("### 維持率割れによる自動縮小（当月）").And.Contain("**発動回数: 1 件**");
        withEvents.Should().NotContain("| # | 時刻 |", "個々の内容は該当日報を参照する（月報は回数のみ）");
        empty.Should().Contain("**発動回数: 0 件**");
    }

    // T-10-205: 全量決済で建玉が無くなった場合、決済後の維持率は「0%」ではなく「建玉なし」と記す。
    [Fact]
    public void 全量決済後の維持率は建玉なしと記す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, "2026-08-04") with
        {
            MarginReductions = [Reduction(new DateTimeOffset(2026, 8, 4, 14, 30, 0, TimeSpan.Zero), ratioAfter: null)],
        });

        md.Should().Contain("建玉なし");
    }

    // 週報は計画が記載を求めていないため節を作らない（求められていない節を勝手に増やさない）。
    [Fact]
    public void 週報には自動縮小の節を作らない()
    {
        var md = ReportRenderer.RenderMarkdown(
            View(ReportKind.Weekly, "2026-W32") with { MarginReductions = [] });

        md.Should().NotContain("維持率割れによる自動縮小");
    }
}
