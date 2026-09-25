using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// T-10-996 / T-10-997, FR-06, FR-10, FR-12, ADR-0040 決定1, #823, IADR-0422 決定3:
// 日報 §4「損切りの実行機構（当日）」——承認時点の手法の集計（純関数）と描画。
//
// 計画（ADR-0040 決定1）: 「どの手法を選んでいるかは、監査ログ・SC-03・日報に出す。選択式にした以上、
// 『いまどれで走っているか』が読めなければ観測結果を解釈できない」。
public class StopLossMethodUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private static OrderApproved Approved(
        StopLossExecutionMethod method, PositionEffect effect = PositionEffect.Open, Guid? decisionId = null) => new(
        decisionId ?? Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m, effect),
        10,
        T0,
        StopLossMethod: method);

    private static ReportView View(ReportKind kind, StopLossMethodUsage? usage) => new()
    {
        Kind = kind,
        PeriodKey = "daily-2026-09-24",
        PeriodLabel = "2026-09-24",
        Markets = ["US"],
        AssumptionsVersion = 1,
        Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
        PolicySummary = "方針",
        Narrative = "散文",
        StopLossMethods = usage,
    };

    // ---- 集計（T-10-996） ----------------------------------------------------------------------

    // 🔴 規則 11 の採用形: 日中に手法を変えた日（S0 → S2）は、**承認ごとの手法で両方を数える**
    // （生成時点の設定値 1 つで塗り潰さない）。
    [Fact]
    public void 日中に手法を変えた日は承認ごとの手法で両方を数える()
    {
        var usage = StopLossMethodUsage.From(
        [
            Approved(StopLossExecutionMethod.BrokerStopOrder),
            Approved(StopLossExecutionMethod.NoProtectiveStop),
            Approved(StopLossExecutionMethod.BrokerStopOrder),
        ]);

        usage.TotalApprovals.Should().Be(3);
        usage.Counts.Should().Equal(
            new StopLossMethodCount(StopLossExecutionMethod.BrokerStopOrder, 2),
            new StopLossMethodCount(StopLossExecutionMethod.NoProtectiveStop, 1));
    }

    // 🔴 否定形: 手仕舞い（owner 手仕舞い・自動縮小）の承認は既定 S0 を運ぶだけで手法が効かない。数えると
    // 「S0 で走った」と読める件数が増える。
    [Fact]
    public void 手仕舞いの承認は数えない()
    {
        var usage = StopLossMethodUsage.From(
        [
            Approved(StopLossExecutionMethod.NoProtectiveStop),
            Approved(StopLossExecutionMethod.BrokerStopOrder, PositionEffect.Close),
            Approved(StopLossExecutionMethod.BrokerStopOrder, PositionEffect.Close),
        ]);

        usage.Counts.Should().ContainSingle()
            .Which.Should().Be(new StopLossMethodCount(StopLossExecutionMethod.NoProtectiveStop, 1));
    }

    [Fact]
    public void 同じ判断の承認が2行あっても1件と数える()
    {
        var id = Guid.NewGuid();

        var usage = StopLossMethodUsage.From(
        [
            Approved(StopLossExecutionMethod.SoftwareStop, decisionId: id),
            Approved(StopLossExecutionMethod.SoftwareStop, decisionId: id),
        ]);

        usage.TotalApprovals.Should().Be(1);
    }

    [Fact]
    public void 手法は序数順に並び_未知の値も落とさず数える()
    {
        var usage = StopLossMethodUsage.From(
        [
            Approved((StopLossExecutionMethod)9),
            Approved(StopLossExecutionMethod.AlternativeBrokerOrderType),
            Approved(StopLossExecutionMethod.SoftwareStop),
        ]);

        usage.Counts.Select(c => c.Method).Should().Equal(
            StopLossExecutionMethod.SoftwareStop,
            StopLossExecutionMethod.AlternativeBrokerOrderType,
            (StopLossExecutionMethod)9);
        StopLossMethodUsage.Label((StopLossExecutionMethod)9).Should().Be("不明(9)");
    }

    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, "S0 ブローカー側逆指値")]
    [InlineData(StopLossExecutionMethod.SoftwareStop, "S1 ソフトウェア逆指値")]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop, "S2 逆指値なしの建玉を許容")]
    [InlineData(StopLossExecutionMethod.AlternativeBrokerOrderType, "S3 他のブローカー側注文種別")]
    public void 表示名は計画の表のID_と手法の名である(StopLossExecutionMethod method, string label)
    {
        StopLossMethodUsage.Label(method).Should().Be(label);
    }

    // ---- 描画（T-10-996 / T-10-997） -----------------------------------------------------------

    [Fact]
    public void 日報の_4_に子節を置き_承認時点の手法ごとの件数を書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, StopLossMethodUsage.From(
        [
            Approved(StopLossExecutionMethod.BrokerStopOrder),
            Approved(StopLossExecutionMethod.NoProtectiveStop),
            Approved(StopLossExecutionMethod.NoProtectiveStop),
        ])));

        md.Should().Contain("### 損切りの実行機構（当日）");
        md.Should().Contain(
            "- **新規建ての承認（承認時点の手法）**: 3 件 — S0 ブローカー側逆指値 1 件 / S2 逆指値なしの建玉を許容 2 件");
        md.Should().Contain("承認の件数であり、発注・約定の件数ではありません。");
        // 子節は §4（リスク統制の記録）の中にあり、§5 より前である。
        md.IndexOf("### 損切りの実行機構（当日）", StringComparison.Ordinal)
            .Should().BeGreaterThan(md.IndexOf("## 4. リスク統制の記録", StringComparison.Ordinal))
            .And.BeLessThan(md.IndexOf("## 5. 市況・特記事項", StringComparison.Ordinal));
        md.Should().NotContain("本文を復元できなかった承認の記録");
    }

    // 🔴 否定形（T-10-997）: 照会できなかったことを「なし」「0 件」と書かない。
    [Fact]
    public void 照会できなければ_なしと書かず照会できなかったと書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, null));

        md.Should().Contain("### 損切りの実行機構（当日）");
        md.Should().Contain("- **承認の記録を照会できませんでした（要確認）**: 「承認なし」とは区別しています。");
        md.Should().NotContain("新規建ての承認（承認時点の手法）");
    }

    // 承認 0 件は「なし」と明記する（空欄と「なし」を区別する）。
    [Fact]
    public void 承認が0件の日はなしと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, StopLossMethodUsage.From([])));

        md.Should().Contain("- **新規建ての承認（承認時点の手法）**: なし（当日の新規建ての承認は 0 件）");
        md.Should().NotContain("照会できませんでした（要確認）**: 「承認なし」");
    }

    [Fact]
    public void 復元できなかった記録があれば件数を書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily, StopLossMethodUsage.From(
            [Approved(StopLossExecutionMethod.SoftwareStop)], unreadableCount: 2)));

        md.Should().Contain("- **新規建ての承認（承認時点の手法）**: 1 件 — S1 ソフトウェア逆指値 1 件");
        md.Should().Contain("- **本文を復元できなかった承認の記録: 2 件**（上の件数に含めていません）");
    }

    // 🔴 否定形（T-10-997）: 計画が求めるのは日報である。週報・月報には出さない（未供給でも出さない）。
    [Theory]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 週報と月報には出さない(ReportKind kind)
    {
        var withData = ReportRenderer.RenderMarkdown(View(kind, StopLossMethodUsage.From(
            [Approved(StopLossExecutionMethod.NoProtectiveStop)])));
        var unsupplied = ReportRenderer.RenderMarkdown(View(kind, null));

        withData.Should().NotContain("損切りの実行機構");
        unsupplied.Should().NotContain("損切りの実行機構");
        ReportInputs.AppliesTo(ReportInput.StopLossMethods, kind).Should().BeFalse();
        ReportInputs.AppliesTo(ReportInput.StopLossMethods, ReportKind.Daily).Should().BeTrue();
    }
}
