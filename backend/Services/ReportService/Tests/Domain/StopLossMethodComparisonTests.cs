using ReportService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Tests;

// T-10-1087 / T-10-1088 / T-10-1089, FR-06, FR-10, ADR-0040 決定1, #1002, IADR-0429 決定3・決定6:
// 選ばれていた手法（承認時点）と実際に適用された手法（発注執行の解決結果）の突き合わせ（純関数）と、日報・月報の描画。
public class StopLossMethodComparisonTests
{
    // 2026-09-24 23:00 JST（14:00Z）。米国の通常取引時間中。
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private static OrderApproved Approved(
        StopLossExecutionMethod method, DateTimeOffset? at = null, Guid? decisionId = null, PositionEffect effect = PositionEffect.Open) => new(
        decisionId ?? Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 200m, effect),
        10,
        at ?? T0,
        StopLossMethod: method);

    private static StopLossMethodResolved Resolved(
        OrderApproved approved, StopLossExecutionMethod? applied, StopLossMethodResolutionReason reason,
        BrokerProvider provider = BrokerProvider.MoomooSimulate, DateTimeOffset? at = null) => new(
        approved.DecisionId, approved.Intent.Symbol, approved.Intent.Market, approved.Intent.ProductType,
        approved.StopLossMethod, applied, reason, provider, at ?? approved.ApprovedAt.AddSeconds(1));

    private static StopLossMethodResolved AsSelected(OrderApproved approved, DateTimeOffset? at = null) =>
        Resolved(approved, approved.StopLossMethod, StopLossMethodResolutionReason.AsSelected, at: at);

    private static ReportView View(ReportKind kind, StopLossMethodUsage? usage, StopLossMethodResolutionFeed? feed) => new()
    {
        Kind = kind,
        PeriodKey = kind == ReportKind.Monthly ? "monthly-2026-09" : "daily-2026-09-24",
        PeriodLabel = kind == ReportKind.Monthly ? "2026-09" : "2026-09-24",
        Markets = ["US"],
        AssumptionsVersion = 1,
        Pnl = new PnlSummary(0m, 0m, 0m, 0m, 0m, 0, 0, 0),
        PolicySummary = "方針",
        Narrative = "散文",
        StopLossMethods = usage,
        StopLossMethodResolutions = feed,
    };

    private static string Section(string md, string heading)
    {
        var start = md.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{heading} が出ていること");
        var next = md.IndexOf("\n#", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? md[start..] : md[start..next];
    }

    // ---- 突き合わせ（T-10-1087） ---------------------------------------------------------------

    // 承認と解決結果は DecisionId で結ぶ。解決結果の記録が無い承認は「未解決」であり一致とも食い違いとも数えない。
    [Fact]
    public void T_10_1087_承認と解決結果をDecisionIdで結び_記録の無い承認は別に数える()
    {
        var s0 = Approved(StopLossExecutionMethod.BrokerStopOrder);
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var missing = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var stranger = Approved(StopLossExecutionMethod.NoProtectiveStop); // 期間外の承認の解決結果（照合で捨てる）

        var comparison = StopLossMethodComparison.From(
            StopLossMethodUsage.From([s0, s2, missing]),
            new StopLossMethodResolutionFeed(
            [
                AsSelected(s0),
                Resolved(s2, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal),
                AsSelected(stranger),
            ]));

        comparison.ApprovalCount.Should().Be(3);
        comparison.ResolvedCount.Should().Be(2);
        comparison.UnresolvedCount.Should().Be(1);
        comparison.AppliedCounts.Should().Equal(
            new StopLossAppliedCount(StopLossExecutionMethod.BrokerStopOrder, 1),
            new StopLossAppliedCount(null, 1));
        comparison.DisagreementCount.Should().Be(1);
        comparison.Disagreements.Should().ContainSingle().Which.Should().Be(new StopLossMethodDisagreement(
            StopLossExecutionMethod.NoProtectiveStop, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate,
            BrokerProvider.MoomooReal, 1));
    }

    // 🔴 同じ DecisionId の解決結果が 2 本あれば、時刻の遅いほう（発注に至った処理）を採る（到着順に依らない）。
    [Fact]
    public void T_10_1087_同じ承認の解決結果が複数あれば時刻の遅いほうを採る()
    {
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var early = Resolved(s2, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal, T0.AddSeconds(1));
        var late = AsSelected(s2, T0.AddMinutes(1));

        foreach (var order in new[] { new[] { early, late }, new[] { late, early } })
        {
            var comparison = StopLossMethodComparison.From(
                StopLossMethodUsage.From([s2]), new StopLossMethodResolutionFeed(order));

            comparison.Outcomes.Should().ContainSingle().Which.Resolution.Should().Be(late);
            comparison.DisagreementCount.Should().Be(0);
        }
    }

    // 🔴 日は承認の JST 暦日。解決が JST 0 時を跨いでも承認の日に数える（解決の時刻の日付では数えない）。
    [Fact]
    public void T_10_1087_日は承認のJST暦日であり解決の時刻ではない()
    {
        // 9/24 23:59:59 JST（14:59:59Z）の承認が、9/25 00:00:02 JST に解決された。
        var lateNight = Approved(StopLossExecutionMethod.NoProtectiveStop, new DateTimeOffset(2026, 9, 24, 14, 59, 59, TimeSpan.Zero));

        var comparison = StopLossMethodComparison.From(
            StopLossMethodUsage.From([lateNight]),
            new StopLossMethodResolutionFeed([AsSelected(lateNight, new DateTimeOffset(2026, 9, 24, 15, 0, 2, TimeSpan.Zero))]));

        comparison.Outcomes.Single().Day.Should().Be(new DateOnly(2026, 9, 24));
        comparison.ResolvedCount.Should().Be(1);
        StopLossMethodComparison.JstDayOf(new DateTimeOffset(2026, 9, 24, 15, 0, 0, TimeSpan.Zero))
            .Should().Be(new DateOnly(2026, 9, 25), "JST 0 時（15:00Z）からは翌日");
    }

    // 月報の日数: 手法ごとの日数（重複あり）・複数の手法が適用された日・食い違った日・未解決の承認を含む日。
    [Fact]
    public void T_10_1087_日数は手法ごとに数え_重複日と食い違った日と未解決の日を別に数える()
    {
        DateTimeOffset Day(int d) => new(2026, 9, d, 14, 0, 0, TimeSpan.Zero);
        var d1a = Approved(StopLossExecutionMethod.BrokerStopOrder, Day(1));
        var d1b = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(1));   // 同じ日に S0 と S2
        var d2 = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(2));    // S2 → 拒否（食い違い）
        var d3 = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(3));    // 解決結果なし
        var d3b = Approved(StopLossExecutionMethod.BrokerStopOrder, Day(3));

        var comparison = StopLossMethodComparison.From(
            StopLossMethodUsage.From([d1a, d1b, d2, d3, d3b]),
            new StopLossMethodResolutionFeed(
            [
                AsSelected(d1a), AsSelected(d1b),
                Resolved(d2, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal),
                AsSelected(d3b),
            ]));

        comparison.ApprovalDays.Should().Be(3);
        // #1006: 日数の内訳は S0〜S3 だけ（見送りは ForgoneDays に別に数える）。
        comparison.AppliedDays.Should().Equal(
            new StopLossAppliedDays(StopLossExecutionMethod.BrokerStopOrder, 2),
            new StopLossAppliedDays(StopLossExecutionMethod.NoProtectiveStop, 1));
        comparison.ForgoneDays.Should().Be(1);
        comparison.MixedDays.Should().Be(1);
        comparison.DisagreementDays.Should().Be(1);
        comparison.UnresolvedDays.Should().Be(1);
    }

    // ---- 日報の描画（T-10-1088） ---------------------------------------------------------------

    [Fact]
    public void T_10_1088_日報は2行を並べ_食い違いを理由つきで書く()
    {
        var s0 = Approved(StopLossExecutionMethod.BrokerStopOrder);
        var s2Real = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var s2Short = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var unknown = Approved((StopLossExecutionMethod)9);

        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily,
            StopLossMethodUsage.From([s0, s2Real, s2Short, unknown]),
            new StopLossMethodResolutionFeed(
            [
                AsSelected(s0),
                Resolved(s2Real, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal),
                Resolved(s2Short, StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.ShortSellEntry),
                Resolved(unknown, StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.UnknownMethod),
            ])));

        var section = Section(md, "### 損切りの実行機構（当日）");
        section.Should().Contain(
            "- **選ばれていた手法（承認時点）**: 計 4 件 — S0 ブローカー側逆指値 1 件 / S2 逆指値なしの建玉を許容 2 件 / 不明(9) 1 件");
        section.Should().Contain(
            "- **実際に適用された手法（発注執行の解決結果）**: 計 4 件 — S0 ブローカー側逆指値 3 件 / 見送り（実際の発注先が SIMULATE でない） 1 件");
        section.Should().Contain("- **選択と実際の食い違い: 3 件** — "
            + "S2 逆指値なしの建玉を許容 → S0 ブローカー側逆指値 1 件（理由: 空売りの新規建ては S0 で扱う） / "
            + "S2 逆指値なしの建玉を許容 → 見送り（実際の発注先が SIMULATE でない） 1 件（理由: 実際の発注先が SIMULATE でないための見送り。実際の発注先: moomoo REAL） / "
            + "不明(9) → S0 ブローカー側逆指値 1 件（理由: 未知の手法の値のため S0 と同じ扱いにした）");
        section.Should().NotContain("解決結果の記録が見つからない承認");
        // 2 行目は 1 行目の直後（計画の並び）。
        section.IndexOf("実際に適用された手法", StringComparison.Ordinal)
            .Should().BeGreaterThan(section.IndexOf("選ばれていた手法", StringComparison.Ordinal));
    }

    [Fact]
    public void T_10_1088_一致した日は食い違いなしと明記する()
    {
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop);

        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily,
            StopLossMethodUsage.From([s2]), new StopLossMethodResolutionFeed([AsSelected(s2)])));

        md.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: 計 1 件 — S2 逆指値なしの建玉を許容 1 件");
        md.Should().Contain("- **選択と実際の食い違い**: なし\n");
    }

    // 承認 0 件の日は両行とも「なし」（空欄と「なし」を区別する）。
    [Fact]
    public void T_10_1088_承認0件の日は両行ともなしと明記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily,
            StopLossMethodUsage.From([]), new StopLossMethodResolutionFeed([])));

        md.Should().Contain("- **選ばれていた手法（承認時点）**: なし（当日の新規建ての承認は 0 件）");
        md.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: なし（当日の新規建ての承認は 0 件）");
        md.Should().NotContain("選択と実際の食い違い");
    }

    // 🔴 否定形: 解決結果を照会できなければ「なし」と書かない（承認 0 件の日も同じ）。1 行目は供給どおりに書く。
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void T_10_1088_解決結果を照会できなければなしと書かない(int approvals)
    {
        var usage = StopLossMethodUsage.From(
            [.. Enumerable.Range(0, approvals).Select(_ => Approved(StopLossExecutionMethod.NoProtectiveStop))]);

        var section = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, usage, null)), "### 損切りの実行機構（当日）");

        section.Should().Contain("- **発注執行の解決結果を照会できませんでした（要確認）**: 「なし」とは区別しています"
            + "（選択と実際の食い違いも判定できていません）。");
        section.Should().NotContain("実際に適用された手法（発注執行の解決結果）**: なし");
        section.Should().NotContain("食い違い**: なし");
    }

    // 🔴 否定形: 承認を照会できなければ 1 行目は #823 の文言のまま、2 行目も「なし」と書かない（対象を決められない）。
    [Fact]
    public void T_10_1088_承認を照会できなければ2行目も照合できないと書く()
    {
        var section = Section(
            ReportRenderer.RenderMarkdown(View(ReportKind.Daily, null, new StopLossMethodResolutionFeed([]))),
            "### 損切りの実行機構（当日）");

        section.Should().Contain("- **承認の記録を照会できませんでした（要確認）**: 「承認なし」とは区別しています。");
        section.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: 照合できません"
            + "（承認の記録を照会できないため、当日の対象を決められません。要確認）");
        section.Should().NotContain("なし（当日の新規建ての承認は 0 件）");
    }

    // 🔴 否定形: 解決結果の記録が無い承認は、一致とも食い違いとも書かない（件数を別に書き、「なし」と言い切らない）。
    [Fact]
    public void T_10_1088_記録の無い承認は一致とも食い違いとも書かない()
    {
        var resolved = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var missing = Approved(StopLossExecutionMethod.NoProtectiveStop);

        var some = ReportRenderer.RenderMarkdown(View(ReportKind.Daily,
            StopLossMethodUsage.From([resolved, missing]), new StopLossMethodResolutionFeed([AsSelected(resolved)], UnreadableCount: 2)));

        some.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: 計 1 件 — S2 逆指値なしの建玉を許容 1 件");
        some.Should().Contain("- **選択と実際の食い違い**: 照合できた承認の範囲ではなし（解決結果の記録が見つからない承認は判定できていません）");
        some.Should().Contain("- **解決結果の記録が見つからない承認: 1 件**（「実際に適用された手法」と食い違いのどちらにも数えていません。");
        some.Should().Contain("- **本文を復元できなかった解決結果の記録: 2 件**（照合に使っていません）");
        some.Should().NotContain("- **選択と実際の食い違い**: なし\n");

        var none = ReportRenderer.RenderMarkdown(View(ReportKind.Daily,
            StopLossMethodUsage.From([missing]), new StopLossMethodResolutionFeed([])));

        none.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: 計 0 件（解決結果の記録が見つかった承認がありません）");
        none.Should().Contain("- **選択と実際の食い違い**: 判定できていません（解決結果の記録が見つかった承認がありません）");
        none.Should().NotContain("発注執行の解決結果）**: なし");
    }

    // ---- 月報の描画（T-10-1089） ---------------------------------------------------------------

    [Fact]
    public void T_10_1089_月報は6の本文に日数ベースの内訳と食い違った日数を置く()
    {
        DateTimeOffset Day(int d) => new(2026, 9, d, 14, 0, 0, TimeSpan.Zero);
        var d1a = Approved(StopLossExecutionMethod.BrokerStopOrder, Day(1));
        var d1b = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(1));
        var d2 = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(2));
        var d3 = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(3));

        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly,
            StopLossMethodUsage.From([d1a, d1b, d2, d3]),
            new StopLossMethodResolutionFeed(
            [
                AsSelected(d1a), AsSelected(d1b),
                Resolved(d2, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal),
            ])));

        var section = Section(md, "### 損切りの実行機構（当月）");
        // #1006: 計画の 1 行（当月の損切りの実行機構: …／選択と実際が食い違った日数: …）。見送りは内訳に含めない。
        section.Should().Contain("- **当月の損切りの実行機構: S0 ブローカー側逆指値 1 日 / S2 逆指値なしの建玉を許容 1 日"
            + "／選択と実際が食い違った日数: 1 日**（新規建ての承認があった日 3 日。複数の手法が適用された日 1 日は各手法に重複して数えています。"
            + "見送り（実際の発注先が SIMULATE でない）の承認は内訳に含めていません。個々の日の内訳と理由は該当日報を参照）");
        section.Should().Contain("- **解決結果の記録が見つからない承認を含む日: 1 日**");
        section.Should().Contain("- 日は日報と同じ JST の暦日（承認の時刻）で数えています。");
        // 明細（個々の日の内訳・理由）は月報に出さない。
        section.Should().NotContain("理由:");
        // §6 の本文（§6.1 より前）に置く。
        md.IndexOf("### 損切りの実行機構（当月）", StringComparison.Ordinal)
            .Should().BeGreaterThan(md.IndexOf("## 6. リスク統制と前提条件の見直し", StringComparison.Ordinal))
            .And.BeLessThan(md.IndexOf("### 6.1 空売りの記録（当月）", StringComparison.Ordinal));
        md.Should().NotContain("損切りの実行機構（当日）");
    }

    [Fact]
    public void T_10_1089_承認の無い月はなしと明記し_食い違いが無ければ0日と書く()
    {
        var none = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly,
            StopLossMethodUsage.From([]), new StopLossMethodResolutionFeed([])));
        none.Should().Contain("- **当月の損切りの実行機構**: なし（当月の新規建ての承認は 0 件）");

        var s0 = Approved(StopLossExecutionMethod.BrokerStopOrder);
        var matched = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly,
            StopLossMethodUsage.From([s0]), new StopLossMethodResolutionFeed([AsSelected(s0)])));
        matched.Should().Contain("- **当月の損切りの実行機構: S0 ブローカー側逆指値 1 日／選択と実際が食い違った日数: 0 日**"
            + "（新規建ての承認があった日 1 日。個々の日の内訳と理由は該当日報を参照）");
        matched.Should().NotContain("重複して数えています");
        matched.Should().NotContain("内訳に含めていません");
    }

    // 🔴 否定形: どちらかを照会できなければ「なし」「0 日」と書かない。
    [Fact]
    public void T_10_1089_照会できなければ月報もなしや0日と書かない()
    {
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop);

        var noApprovals = Section(
            ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, null, new StopLossMethodResolutionFeed([]))),
            "### 損切りの実行機構（当月）");
        noApprovals.Should().Contain("- **承認の記録を照会できませんでした（要確認）**: 「承認なし」とは区別しています");
        noApprovals.Should().NotContain("なし（当月の新規建ての承認は 0 件）");
        noApprovals.Should().NotContain("0 日");

        var noResolutions = Section(
            ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, StopLossMethodUsage.From([s2]), null)),
            "### 損切りの実行機構（当月）");
        noResolutions.Should().Contain("- **新規建ての承認があった日**: 1 日");
        noResolutions.Should().Contain("- **発注執行の解決結果を照会できませんでした（要確認）**: 「なし」とは区別しています");
        noResolutions.Should().NotContain("食い違った日数:");
    }

    // 🔴 否定形（Principle A）: 承認はあるのに解決結果が 1 件も見つからない月（監査の購読が止まっていた等）は、
    // 食い違った日数を「0 日」と書かず「判定できていません」と書く（日報の食い違いの行と同じ扱い）。
    [Fact]
    public void T_10_1089_解決結果が1件も見つからない月は食い違った日数を0日と書かない()
    {
        DateTimeOffset Day(int d) => new(2026, 9, d, 14, 0, 0, TimeSpan.Zero);
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly,
            StopLossMethodUsage.From(
                [Approved(StopLossExecutionMethod.NoProtectiveStop, Day(1)), Approved(StopLossExecutionMethod.BrokerStopOrder, Day(2))]),
            new StopLossMethodResolutionFeed([])));

        var section = Section(md, "### 損切りの実行機構（当月）");
        section.Should().Contain("- **当月の損切りの実行機構**: 数えられません／**選択と実際が食い違った日数**: 判定できていません"
            + "（解決結果の記録が見つかった承認がありません。新規建ての承認があった日 2 日）");
        section.Should().Contain("- **解決結果の記録が見つからない承認を含む日: 2 日**");
        section.Should().NotContain("0 日");
    }

    // 🔴 否定形（planning#644 の裁定 2）: 週報には出さない（供給があっても）。
    [Fact]
    public void T_10_1089_週報には出さない()
    {
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop);

        ReportRenderer.RenderMarkdown(View(ReportKind.Weekly,
                StopLossMethodUsage.From([s2]), new StopLossMethodResolutionFeed([AsSelected(s2)])))
            .Should().NotContain("損切りの実行機構");
        ReportInputs.AppliesTo(ReportInput.StopLossMethodResolutions, ReportKind.Weekly).Should().BeFalse();
    }

    // ---- 改定後のテンプレート（#1006・planning#646 の裁定。T-10-1110〜T-10-1112） --------------------------

    private static OrderApproved ApprovedFor(
        StopLossExecutionMethod method, ProductType productType, DateTimeOffset at) => new(
        Guid.NewGuid(),
        new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, productType, BrokerProvider.MoomooSimulate, 10, 200m),
        10,
        at,
        StopLossMethod: method);

    // 計画 04_report-templates 日報 §4（2026-09-25 訂正）: 2 行目は S0〜S3 に「見送り（実際の発注先が SIMULATE でない）」を加える。
    // 食い違いの理由は「実際の発注先が SIMULATE でないための見送り」の語で書く。S1・S3 は選択どおりに執行される（IADR-0344・IADR-0347）。
    [Fact]
    public void T_10_1110_日報は見送りの区分名と理由をテンプレートの語で書き_S1とS3を選択どおりに数える()
    {
        var s0 = Approved(StopLossExecutionMethod.BrokerStopOrder);
        var s1 = Approved(StopLossExecutionMethod.SoftwareStop);
        var s1Real = Approved(StopLossExecutionMethod.SoftwareStop);
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop);
        var s3 = Approved(StopLossExecutionMethod.AlternativeBrokerOrderType);
        var s3Paper = Approved(StopLossExecutionMethod.AlternativeBrokerOrderType);

        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily,
            StopLossMethodUsage.From([s0, s1, s1Real, s2, s3, s3Paper]),
            new StopLossMethodResolutionFeed(
            [
                AsSelected(s0), AsSelected(s1), AsSelected(s2), AsSelected(s3),
                Resolved(s1Real, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal),
                Resolved(s3Paper, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.InternalPaper),
            ])));

        var section = Section(md, "### 損切りの実行機構（当日）");
        section.Should().Contain("- **選ばれていた手法（承認時点）**: 計 6 件 — S0 ブローカー側逆指値 1 件 / S1 ソフトウェア逆指値 2 件 / "
            + "S2 逆指値なしの建玉を許容 1 件 / S3 他のブローカー側注文種別 2 件\n");
        section.Should().Contain("- **実際に適用された手法（発注執行の解決結果）**: 計 6 件 — S0 ブローカー側逆指値 1 件 / S1 ソフトウェア逆指値 1 件 / "
            + "S2 逆指値なしの建玉を許容 1 件 / S3 他のブローカー側注文種別 1 件 / 見送り（実際の発注先が SIMULATE でない） 2 件\n");
        section.Should().Contain("- **選択と実際の食い違い: 2 件** — "
            + "S1 ソフトウェア逆指値 → 見送り（実際の発注先が SIMULATE でない） 1 件（理由: 実際の発注先が SIMULATE でないための見送り。実際の発注先: moomoo REAL） / "
            + "S3 他のブローカー側注文種別 → 見送り（実際の発注先が SIMULATE でない） 1 件（理由: 実際の発注先が SIMULATE でないための見送り。実際の発注先: 内蔵 paper）\n");
        // 🔴 否定形: 旧い区分名・旧い理由の語を出さない。固定の注記で「見送り」の 2 つの意味を書き分ける。
        section.Should().NotContain("発注せず（拒否）");
        section.Should().NotContain("発注しなかった。");
        section.Should().Contain("（「見送り（実際の発注先が SIMULATE でない）」は解決の時点で発注しなかった承認です。"
            + "解決の後に発注を見送った場合〔逆指値価格が無い等〕や約定の有無は反映しません）");
    }

    // 計画 04_report-templates 月報 §6（2026-09-25 訂正）: 「当月の損切りの実行機構: <S0 a 日 / S1 b 日 / S2 c 日 / S3 d 日>
    // ／選択と実際が食い違った日数: <n 日>」の 1 行。内訳は S0〜S3 の 4 区分（見送りは内訳に数えない）。
    [Fact]
    public void T_10_1111_月報は1行で_内訳はS0からS3の4区分であり見送りを数えない()
    {
        DateTimeOffset Day(int d) => new(2026, 9, d, 14, 0, 0, TimeSpan.Zero);

        // 4 区分がすべて現れる月（並びは S0→S3。入力の順に依らない）＋ 見送りの日。
        var s3 = Approved(StopLossExecutionMethod.AlternativeBrokerOrderType, Day(1));
        var s2 = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(2));
        var s1 = Approved(StopLossExecutionMethod.SoftwareStop, Day(3));
        var s0 = Approved(StopLossExecutionMethod.BrokerStopOrder, Day(4));
        var s1Real = Approved(StopLossExecutionMethod.SoftwareStop, Day(5));
        var all = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Monthly,
            StopLossMethodUsage.From([s3, s2, s1, s0, s1Real]),
            new StopLossMethodResolutionFeed(
            [
                AsSelected(s3), AsSelected(s2), AsSelected(s1), AsSelected(s0),
                Resolved(s1Real, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal),
            ]))), "### 損切りの実行機構（当月）");

        all.Should().Contain("- **当月の損切りの実行機構: S0 ブローカー側逆指値 1 日 / S1 ソフトウェア逆指値 1 日 / "
            + "S2 逆指値なしの建玉を許容 1 日 / S3 他のブローカー側注文種別 1 日／選択と実際が食い違った日数: 1 日**"
            + "（新規建ての承認があった日 5 日。見送り（実際の発注先が SIMULATE でない）の承認は内訳に含めていません。"
            + "個々の日の内訳と理由は該当日報を参照）\n");
        // 🔴 否定形: 見送りを日数の区分として出さない。旧い行名（2 行構成）も出さない。
        all.Should().NotContain("SIMULATE でない） 1 日");
        all.Should().NotContain("実際に適用された手法の日数");
        all.Should().NotContain("- **選択と実際が食い違った日数");

        // 解決結果はあるがすべて見送りの月: 空の内訳を出さず、その旨を書く。
        var forgone = Approved(StopLossExecutionMethod.NoProtectiveStop, Day(6));
        var allForgone = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Monthly,
            StopLossMethodUsage.From([forgone]),
            new StopLossMethodResolutionFeed(
                [Resolved(forgone, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate, BrokerProvider.MoomooReal)]))),
            "### 損切りの実行機構（当月）");

        allForgone.Should().Contain("- **当月の損切りの実行機構: S0〜S3 のいずれも適用されませんでした（解決結果はすべて見送り）"
            + "／選択と実際が食い違った日数: 1 日**（新規建ての承認があった日 1 日。個々の日の内訳と理由は該当日報を参照）\n");
        allForgone.Should().NotContain("実行機構: ／");
    }

    // 計画の記載要件「内訳の合計は『計 n 件』と一致させる」を、日報の 2 行それぞれについて多数の組合せで確かめる
    // （S0〜S3・未知の値 × 現物／空売り × 発注先 3 × 解決結果の有無。種固定で決定的）。月報の内訳は S0〜S3 だけで、並びは S0→S3。
    [Fact]
    public void T_10_1112_日報の2行はそれぞれ内訳の合計が計と一致し_月報の内訳はS0からS3だけ()
    {
        var methods = new[]
        {
            StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.SoftwareStop,
            StopLossExecutionMethod.NoProtectiveStop, StopLossExecutionMethod.AlternativeBrokerOrderType,
            (StopLossExecutionMethod)9,
        };
        var providers = new[] { BrokerProvider.MoomooSimulate, BrokerProvider.MoomooReal, BrokerProvider.InternalPaper };
        string[] knownLabels = [.. methods.Take(4).Select(StopLossMethodUsage.Label)];
        string[] selectedOrder = [.. knownLabels, StopLossMethodUsage.Label((StopLossExecutionMethod)9)];
        string[] appliedOrder = [.. knownLabels, StopLossMethodComparison.ForgoneLabel];

        var random = new Random(1006);
        var checkedDays = 0;
        for (var run = 0; run < 300; run++)
        {
            var approvals = new List<OrderApproved>();
            var resolutions = new List<StopLossMethodResolved>();
            var count = random.Next(0, 13);
            for (var i = 0; i < count; i++)
            {
                var method = methods[random.Next(methods.Length)];
                var product = random.Next(4) == 0 ? ProductType.ShortSell : ProductType.Cash;
                var approved = ApprovedFor(method, product, T0.AddDays(random.Next(0, 5)));
                approvals.Add(approved);
                if (random.Next(6) == 0)
                    continue; // 解決結果の記録が見つからない承認
                var provider = providers[random.Next(providers.Length)];
                var (applied, reason) = ResolveLikePolicy(method, product, provider);
                resolutions.Add(Resolved(approved, applied, reason, provider));
            }

            var usage = StopLossMethodUsage.From(approvals);
            var feed = new StopLossMethodResolutionFeed(resolutions);
            var daily = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Daily, usage, feed)), "### 損切りの実行機構（当日）");
            var comparison = StopLossMethodComparison.From(usage, feed);

            if (count == 0)
            {
                daily.Should().Contain("選ばれていた手法（承認時点）**: なし");
                continue;
            }

            var (selectedTotal, selectedItems) = ParseRow(daily, "選ばれていた手法（承認時点）");
            selectedTotal.Should().Be(count);
            selectedItems.Sum(x => x.Count).Should().Be(selectedTotal, $"1 行目の内訳の合計は計と一致する（run {run}）:\n{daily}");
            AssertCategories(selectedItems, selectedOrder, run, daily);

            if (comparison.ResolvedCount > 0)
            {
                var (appliedTotal, appliedItems) = ParseRow(daily, "実際に適用された手法（発注執行の解決結果）");
                appliedTotal.Should().Be(comparison.ResolvedCount);
                appliedItems.Sum(x => x.Count).Should().Be(appliedTotal, $"2 行目の内訳の合計は計と一致する（run {run}）:\n{daily}");
                AssertCategories(appliedItems, appliedOrder, run, daily);
                // 1 行目と 2 行目の計の差は「解決結果の記録が見つからない承認」の件数（別の行）が説明する。
                (selectedTotal - appliedTotal).Should().Be(comparison.UnresolvedCount);
            }

            var monthly = Section(ReportRenderer.RenderMarkdown(View(ReportKind.Monthly, usage, feed)), "### 損切りの実行機構（当月）");
            if (comparison.AppliedDays.Count > 0)
            {
                var line = monthly.Split('\n').Single(l => l.StartsWith("- **当月の損切りの実行機構: ", StringComparison.Ordinal));
                var body = line["- **当月の損切りの実行機構: ".Length..line.IndexOf('／', StringComparison.Ordinal)];
                var days = body.Split(" / ").Select(item =>
                {
                    var cut = item.LastIndexOf(' ', item.Length - " 日".Length - 1);
                    item.Should().EndWith(" 日");
                    return (Label: item[..cut], Count: int.Parse(item[(cut + 1)..^" 日".Length], System.Globalization.CultureInfo.InvariantCulture));
                }).ToList();
                AssertCategories(days, knownLabels, run, monthly);
                checkedDays++;
            }
        }

        checkedDays.Should().BeGreaterThan(100, "月報の内訳を検査した回が十分にある");

        static (StopLossExecutionMethod? Applied, StopLossMethodResolutionReason Reason) ResolveLikePolicy(
            StopLossExecutionMethod method, ProductType product, BrokerProvider provider)
        {
            // 発注執行の解決規則（StopLossMethodPolicy.ResolveWithReason）の順序を写した試験データの生成器。
            if (method == StopLossExecutionMethod.BrokerStopOrder)
                return (method, StopLossMethodResolutionReason.AsSelected);
            if (provider != BrokerProvider.MoomooSimulate)
                return (null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate);
            if (product == ProductType.ShortSell)
                return (StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.ShortSellEntry);
            return Enum.IsDefined(method)
                ? (method, StopLossMethodResolutionReason.AsSelected)
                : (StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.UnknownMethod);
        }

        static (int Total, List<(string Label, int Count)> Items) ParseRow(string section, string label)
        {
            var prefix = $"- **{label}**: 計 ";
            var line = section.Split('\n').Single(l => l.StartsWith(prefix, StringComparison.Ordinal));
            var rest = line[prefix.Length..];
            var total = int.Parse(rest[..rest.IndexOf(' ', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
            var items = rest[(rest.IndexOf(" — ", StringComparison.Ordinal) + " — ".Length)..].Split(" / ").Select(item =>
            {
                item.Should().EndWith(" 件");
                var cut = item.LastIndexOf(' ', item.Length - " 件".Length - 1);
                return (item[..cut], int.Parse(item[(cut + 1)..^" 件".Length], System.Globalization.CultureInfo.InvariantCulture));
            }).ToList();
            return (total, items);
        }

        static void AssertCategories(List<(string Label, int Count)> items, string[] order, int run, string text)
        {
            // 区分は既知の名前だけ・件数 0 の区分は出さない・重複なし・並びは計画の順。
            var positions = items.Select(x => Array.IndexOf(order, x.Label)).ToList();
            positions.Should().NotContain(-1, $"区分名は計画の区分に限る（run {run}）:\n{text}");
            positions.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
            items.Should().OnlyContain(x => x.Count > 0);
        }
    }
}
