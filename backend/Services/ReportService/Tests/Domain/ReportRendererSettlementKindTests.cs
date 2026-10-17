using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using ReportService.Domain;
using Xunit;

namespace ReportService.Tests;

// FR-06, FR-10, FR-16, UC-04, #1362, IADR-0552: 週報の本文（§1「うち損切り」・§3 ハイライト・§5「損切り執行」）が、
// 散文と同じ決済の種別（FillPnlAttribution.Kind＝SettlementKinds.Of）を描く。出口（ReportRenderer の本文）だけを見る。
public class ReportRendererSettlementKindTests
{
    private static readonly DateTimeOffset Mon = new(2026, 10, 12, 23, 0, 0, TimeSpan.FromHours(9));

    private static ReportView Weekly(IReadOnlyList<FillPnlAttribution>? attributions, bool fillsUnsupplied = false) => new()
    {
        Kind = ReportKind.Weekly,
        PeriodKey = "weekly-2026-W42",
        PeriodLabel = "2026-W42",
        Markets = ["US"],
        AssumptionsVersion = 3,
        Pnl = new PnlSummary(2_000m, 100m, 380m, 1_520m, 0m, 4, 2, 1),
        Narrative = "散文。",
        PolicySummary = "方針。",
        FillAttributions = attributions,
        FillsUnsupplied = fillsUnsupplied,
    };

    private static FillPnlAttribution Settle(
        int sequence, int addDays, string symbol, decimal realized, SettlementKind? kind, bool unvalued = false) =>
        new(sequence, Mon.AddDays(addDays), DateOnly.FromDateTime(Mon.AddDays(addDays).DateTime),
            Market.UnitedStates, symbol, TradeSide.Sell, Quantity: 10, Price: 100m, Cost: 1m,
            RealizedPnlGross: realized, Realizing: true, Rationale: null, Unvalued: unvalued, Kind: kind);

    private static FillPnlAttribution Open(int sequence) =>
        new(sequence, Mon, DateOnly.FromDateTime(Mon.DateTime), Market.UnitedStates, "OPEN", TradeSide.Buy,
            Quantity: 10, Price: 100m, Cost: 1m, RealizedPnlGross: 0m, Realizing: false, Rationale: null);

    private static string Section(string markdown, string from, string to)
    {
        var start = markdown.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = markdown.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return markdown[start..end];
    }

    // A1・A3: 最良・最悪に種別が出る。由来の無い決済は「種別未供給」（推測しない）。
    [Fact]
    public void ハイライトの最良と最悪に決済の種別を描き由来の無い決済は種別未供給と描く()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(
        [
            Open(1),
            Settle(2, 1, "TSLA", -7_000m, SettlementKind.StopLoss),
            Settle(3, 2, "MSFT", 1_220m, null),
        ]));
        var section = Section(md, "## 3. ハイライト取引", "## 4.");

        section.Should().Contain("- **最悪**: TSLA（US） -7,000.00 USD — 2026-10-13 決済（該当日報: `daily-2026-10-13`） — 種別: 損切り — 原因:");
        section.Should().Contain("- **最良**: MSFT（US） +1,220.00 USD — 2026-10-14 決済（該当日報: `daily-2026-10-14`） — 種別: 種別未供給 — 判断の要点:");
        section.Should().Contain("「種別未供給」は種別を記録から取れなかった決済で、**推測で埋めていません。**");
    }

    // A2: §1 と §5 が同じ件数。種別未供給の決済があれば件数を併記し、損切りに数えない。
    [Fact]
    public void 損切りの件数を週間サマリとリスク費用レビューに同じ値で描き種別未供給を併記する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(
        [
            Open(1),
            Settle(2, 1, "TSLA", -7_000m, SettlementKind.StopLoss),
            Settle(3, 1, "AMZN", -300m, SettlementKind.DecisionExit),
            Settle(4, 2, "NVDA", 10m, null),
            Settle(5, 3, "META", -50m, SettlementKind.ProtectionLostClose),
        ]));

        const string cell = "1 件（ほかに種別未供給の決済 1 件。その中に損切りがあり得ます）";
        md.Should().Contain($"| うち損切り（決済の種別） | {cell} |");
        Section(md, "## 5. リスク・費用レビュー", "## 6.").Should().Contain($"- 損切り執行: {cell} / 発注拒否: **未供給**");
        md.Should().Contain("保護逆指値が成立しないときの手仕舞いは損切りに数えていません。");
    }

    // A6: 損益を算定できなかった損切りも数え、その件数を明記する。
    [Fact]
    public void 損益を算定できなかった損切りも数えて件数を明記する()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly(
        [
            Settle(1, 0, "TSLA", -7_000m, SettlementKind.StopLoss),
            // S1 の決済も「損切り」（SettlementKinds.Of の表）。
            Settle(2, 1, "AAPL", 0m, SettlementKinds.Of(FillApprovalSource.SoftwareStopS1, null), unvalued: true),
        ]));

        md.Should().Contain("| うち損切り（決済の種別） | 2 件（うち 1 件は期間より前に建てた建玉の決済で損益を算定できていません） |");
    }

    // A5: 決済 0 件の週は 0 件（事実）。
    [Fact]
    public void 決済が無い週は損切り0件と描く()
    {
        var md = ReportRenderer.RenderMarkdown(Weekly([Open(1)]));

        md.Should().Contain("| うち損切り（決済の種別） | 0 件 |");
        md.Should().Contain("- 損切り執行: 0 件 / 発注拒否: **未供給**");
    }

    // A4: 帰属が無い・約定を照会できなかった週は未供給（0 件と書かない）。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 約定を取得できなかった週は損切りを未供給と描き0件と書かない(bool attributionsAreEmptyList)
    {
        var view = attributionsAreEmptyList ? Weekly([], fillsUnsupplied: true) : Weekly(null);
        var md = ReportRenderer.RenderMarkdown(view);

        md.Should().Contain("| うち損切り（決済の種別） | **未供給** |");
        md.Should().Contain("- 損切り執行: **未供給** / 発注拒否: **未供給**");
        md.Should().Contain("当週の約定を取得できなかったため、決済の種別から数えられていません。**「損切りが 0 件だった」ではありません。**");
        md.Should().NotContain("損切り執行: 0 件");
    }

    // 週報以外の §1 に行を足さない（日報・月報の表は計画どおり）。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Monthly)]
    public void 週報以外のサマリにうち損切りの行を出さない(ReportKind kind)
    {
        var md = ReportRenderer.RenderMarkdown(Weekly([Settle(1, 0, "TSLA", -1m, SettlementKind.StopLoss)]) with { Kind = kind });

        md.Should().NotContain("うち損切り（決済の種別）");
    }
}

