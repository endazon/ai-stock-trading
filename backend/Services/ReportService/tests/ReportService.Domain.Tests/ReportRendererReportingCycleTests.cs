using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace ReportService.Domain.Tests;

// FR-06, FR-07, FR-16, FR-17, #338, 04_report-templates（拡張後）, 04_workflows/03_reporting-cycle,
// INDEX 決定29 / 34 / 43 / 44 / 45, ADR-0016 決定15, ADR-0017 決定2・決定4:
// 報告サイクルの拡張分（三者比較・統制作動状況・LLM 利用実績・為替差損益・空売り・稼働率・縮退件数）の描画。
//
// 🔴 **本ファイルの規律**: 「未供給のとき出ない／未供給と書く」という**否定形だけを置かない**。
// 対の肯定形（供給されたときに正しく出る）を必ず添える。不在の表明だけでは、節ごと壊れていても緑になる。
public class ReportRendererReportingCycleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static PnlSummary Pnl() => new(2_000m, 100m, 380m, 1_520m, -300m, 3, 4, 3);

    private static ReportView View(ReportKind kind) => new()
    {
        Kind = kind,
        PeriodKey = $"{kind.ToString().ToLowerInvariant()}-2026-08",
        PeriodLabel = kind == ReportKind.Monthly ? "2026-08" : "2026-08-28",
        Markets = ["US"],
        AssumptionsVersion = 3,
        BasedOn = "weekly-2026-W35",
        Pnl = Pnl(),
        PolicySummary = "方針テスト",
        Narrative = "散文テスト",
    };

    // --- 機密区分（INDEX 決定43） ---

    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Weekly)]
    [InlineData(ReportKind.Monthly)]
    public void 全報告書のフロントマターに機密区分internalを持つ(ReportKind kind)
    {
        ReportRenderer.RenderMarkdown(View(kind)).Should().Contain("confidentiality: internal");
    }

    // --- 目標値の YAML ブロック併記（INDEX 決定29） ---

    [Fact]
    public void 方針の直後に機械可読なYAMLブロックを併記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily));

        md.Should().Contain("```yaml");
        md.IndexOf("```yaml", StringComparison.Ordinal)
            .Should().BeGreaterThan(md.IndexOf("## 3. 翌営業日の方針", StringComparison.Ordinal));
    }

    // --- 為替差損益（独立表示） ---

    // 🔴 **否定形**: 供給が無い期間を「0 円」と書かない。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Monthly)]
    public void 為替差損益は未供給ならゼロ円と書かない(ReportKind kind)
    {
        var md = ReportRenderer.RenderMarkdown(View(kind));

        md.Should().Contain("為替差損益（独立表示） | **供給されていません**（0 円ではありません）");
    }

    // **対の肯定形**: 供給されたら円建てで独立行に出る。
    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Monthly)]
    public void 為替差損益は供給されれば円建ての独立行に出る(ReportKind kind)
    {
        var md = ReportRenderer.RenderMarkdown(View(kind) with { FxTranslation = new FxTranslationSummary(-1_234m, 5) });

        md.Should().Contain("為替差損益（独立表示） | -1,234 JPY（明細 5 件）");
        // 取引損益（USD）と同じ行に混ざっていない＝独立行である。
        md.Should().Contain("実現損益（税引後・費用込み） | +1,520.00 USD");
    }

    // 週報には為替差損益の行を持たない（計画が週報に求めていない）。
    [Fact]
    public void 週報には為替差損益の行を置かない()
    {
        ReportRenderer.RenderMarkdown(View(ReportKind.Weekly)).Should().NotContain("為替差損益");
    }

    // --- 日報: OpenD 稼働率と取引判断スキップ（INDEX 決定34 / ADR-0017 決定2） ---

    [Fact]
    public void 日報の稼働率は未供給ならゼロパーセントと書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily));

        md.Should().Contain("OpenD 稼働率（当日の通常取引時間に対する比率） | **供給されていません**（稼働率 0% ではありません）");
    }

    [Theory]
    [InlineData(0.75, "75.0% — Stage 1 の日数算入: 算入（50% 以上）")]
    [InlineData(0.50, "50.0% — Stage 1 の日数算入: 算入（50% 以上）")] // 境界
    [InlineData(0.49, "49.0% — Stage 1 の日数算入: 非算入（50% 未満）")]
    public void 日報の稼働率は算入可否とともに出る(double ratio, string expected)
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily) with
        {
            Uptime = new OpenDUptimeRecord([new OpenDUptimeDay(new DateOnly(2026, 8, 28), (decimal)ratio)]),
        });

        md.Should().Contain(expected);
    }

    [Fact]
    public void 日報の取引判断スキップは未供給ならゼロ件と書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily));

        md.Should().Contain("モデル利用不能による取引判断スキップ | **供給されていません**（0 件ではありません）");
    }

    [Fact]
    public void 日報の取引判断スキップは供給されれば事由別に出る()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Daily) with
        {
            LlmUsage = new LlmUsageRecord([], [],
            [
                new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0),
                new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0),
            ]),
        });

        md.Should().Contain($"モデル利用不能による取引判断スキップ | 2 件（{TradeDecisionSkipReasons.ModelUnavailable}: 2 件）");
    }

    // --- 空売りの記録（ADR-0016 決定15 / ADR-0027 決定4） ---

    [Theory]
    [InlineData(ReportKind.Daily)]
    [InlineData(ReportKind.Monthly)]
    public void 空売りの記録は未供給ならゼロUSDと書かない(ReportKind kind)
    {
        var md = ReportRenderer.RenderMarkdown(View(kind));

        md.Should().Contain("**借株コストを照会できませんでした（供給元がありません）**");
        md.Should().Contain("「0 USD」とは区別しています");
    }

    [Fact]
    public void 空売り建玉が無い期間はゼロ件と明記する()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with { BorrowFees = new BorrowFeeRecord([], []) });

        md.Should().Contain("**空売り建玉: 0 件**");
    }

    // 🔴 **未計上の日を 0 円として合計へ混ぜない**ことが、報告書の文面としても出る。
    [Fact]
    public void 借株コストは合計と銘柄別内訳と未計上件数を出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            BorrowFees = new BorrowFeeRecord(
                [new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m, T0)],
                [new BorrowFeeAccrualUnavailable("TSLA", Market.UnitedStates, new DateOnly(2026, 8, 4), "照会失敗", T0)]),
        });

        md.Should().Contain("**借株コスト（経費区分 BorrowFee）合計: +1.64 USD**（計上 1 件）");
        md.Should().Contain("適用年率の最大: 6.0%（上限 20.0%）");
        md.Should().Contain("**料率を取得できず未計上だった日: 1 件**");
        md.Should().Contain("**実際の借株コストは上の合計より大きくなります**");
        md.Should().Contain("| AAPL | +1.64 USD | 6.0% |");
    }

    // 週報には空売りの節を置かない（計画が求めていない）。
    [Fact]
    public void 週報には空売りの節を置かない()
    {
        ReportRenderer.RenderMarkdown(View(ReportKind.Weekly)).Should().NotContain("### 空売りの記録");
    }

    // --- 🔴 統制作動状況（月報 §6・本 issue の中核） ---

    [Fact]
    public void 統制作動状況は月報にのみ出る()
    {
        ReportRenderer.RenderMarkdown(View(ReportKind.Monthly)).Should().Contain("### 当月の統制作動状況");
        ReportRenderer.RenderMarkdown(View(ReportKind.Daily)).Should().NotContain("### 当月の統制作動状況");
        ReportRenderer.RenderMarkdown(View(ReportKind.Weekly)).Should().NotContain("### 当月の統制作動状況");
    }

    // 🔴 **「作動機会がなかった統制」と「違反 0 件」が別の見出しで出る。**
    [Fact]
    public void 作動機会なしと違反ゼロを別の見出しで出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            MarginReductions = [],
            BuyInInferences = [],
            BorrowFees = new BorrowFeeRecord([], []),
            FxSourceStatus = new FxSourceStatus([], [], [], [], [], [new FxRateSourceUsed("USD", "boj", 1, 2, T0)]),
        });

        md.Should().Contain("1. 作動機会があり、作動しなかった統制（この統制については違反 0 件を主張できる）");
        md.Should().Contain("2. **作動機会そのものが存在しなかった統制（未検証）**");

        // 空売り建玉が無い月: 空売り由来の統制は「未検証」の側に、為替は「機会あり」の側に出る。
        var noOpportunityIndex = md.IndexOf("2. **作動機会そのものが存在しなかった統制（未検証）**", StringComparison.Ordinal);
        var activatedIndex = md.IndexOf("3. 当月に作動した統制", StringComparison.Ordinal);
        var noOpportunityBlock = md[noOpportunityIndex..activatedIndex];

        noOpportunityBlock.Should().Contain(ControlActivationCatalog.BorrowFeeRateCap);
        noOpportunityBlock.Should().Contain(ControlActivationCatalog.BuyInDetection);
        // 🔴 否定形: 為替の統制は機会があったので「未検証」の一覧へ混ざらない。
        noOpportunityBlock.Should().NotContain(ControlActivationCatalog.FxStalenessEntryBlock);
    }

    // 🔴 **空の一覧でも見出しごと消さない。** 節が無いことは「該当なし」とも「出し忘れ」とも読めるため。
    [Fact]
    public void 統制の各分類は空でも見出しを残す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly));

        md.Should().Contain("1. 作動機会があり、作動しなかった統制");
        md.Should().Contain("2. **作動機会そのものが存在しなかった統制（未検証）**");
        md.Should().Contain("3. 当月に作動した統制");
        md.Should().Contain("4. **判定に要る記録を照会できず、判定できなかった統制**");
    }

    // 判定に要る記録が無い統制は 4 番目へ出る（1・2 のどちらにも入らない）。
    [Fact]
    public void 記録を照会できない統制は判定不能の一覧へ出る()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly));

        var notSuppliedIndex = md.IndexOf("4. **判定に要る記録を照会できず、判定できなかった統制**", StringComparison.Ordinal);
        md[notSuppliedIndex..].Should().Contain(ControlActivationCatalog.MaintenanceMarginReduction);

        // 否定形: 未供給が「未検証（機会なし）」へ倒れていない。
        md.Should().Contain("2. **作動機会そのものが存在しなかった統制（未検証）**\n\n- 該当なし");
    }

    // --- 稼働率分布（月報 §6.2・INDEX 決定34） ---

    [Fact]
    public void 月報の稼働率分布は未供給なら日数を書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly));

        md.Should().Contain("**稼働率の観測を照会できませんでした（供給元がありません）**");
        md.Should().Contain("「稼働率 0%」「算入 0 日」とは区別しています");
    }

    [Fact]
    public void 月報の稼働率分布は三区分の日数と累計算入日数を出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            Uptime = new OpenDUptimeRecord(
            [
                new OpenDUptimeDay(new DateOnly(2026, 8, 3), 1.0m),
                new OpenDUptimeDay(new DateOnly(2026, 8, 4), 0.60m),
                new OpenDUptimeDay(new DateOnly(2026, 8, 5), 0.20m),
            ], Stage1CumulativeCountedDays: 41),
        });

        md.Should().Contain("| 100% | 1 日 |");
        md.Should().Contain("| 50〜99%（Stage 1 の日数に算入する） | 1 日 |");
        md.Should().Contain("| 50% 未満（Stage 1 の日数に算入しない） | 1 日 |");
        md.Should().Contain("- Stage 1 の累計算入日数: 41 / 60 日");
    }

    // 累計が供給されない場合、当月の算入日数を**累計と偽らない**。
    [Fact]
    public void 累計算入日数が未供給なら当月の算入日数を累計と偽らない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            Uptime = new OpenDUptimeRecord([new OpenDUptimeDay(new DateOnly(2026, 8, 3), 1.0m)]),
        });

        md.Should().Contain("Stage 1 の累計算入日数: **供給されていません**");
        md.Should().Contain("**累計ではありません**");
    }

    // --- 三者比較（月報 §5） ---

    [Fact]
    public void 三者比較は未供給ならゼロ件と書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly));

        md.Should().Contain("**三者比較の実績を照会できませんでした（供給元がありません）**");
        md.Should().Contain("**走らせた結果 0 だったのではありません。**");
    }

    // 🔴 **「空欄」と「値が 0」を区別できる表記にする**（計画 §5 の明文）。
    [Fact]
    public void 三者比較は該当なしと値ゼロを区別して出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            ThreeWayComparison = new ThreeWayComparison(
                WinRate: new ThreeWayMetric(0.55m, 0m, null),
                AveragePnlUsd: new ThreeWayMetric(12.5m, 0m, null),
                MaxDrawdown: new ThreeWayMetric(0.08m, 0m, null),
                TradeCount: new ThreeWayMetric(120m, 0m, null),
                DivergenceNote: "③ 執行の差が勝率に出ている。"),
        });

        // バックテスト＝値あり / SIMULATE＝値 0 / 実弾＝該当なし（空欄）
        md.Should().Contain("| 勝率 | 55.0% | 0.0% | 該当なし |");
        md.Should().Contain("| 取引件数 | 120 件 | 0 件 | 該当なし |");
        md.Should().Contain("| 平均損益 | +12.50 USD | 0.00 USD | 該当なし |");
        md.Should().Contain("- 差分が大きい指標の要因考察: ③ 執行の差が勝率に出ている。");
        md.Should().Contain("「該当なし」はその段をまだ走らせていないことを表す。**値 0 とは区別する。**");
    }

    [Fact]
    public void 三者比較の考察が無ければ未記入と書く()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            ThreeWayComparison = new ThreeWayComparison(
                new ThreeWayMetric(null, null, null), new ThreeWayMetric(null, null, null),
                new ThreeWayMetric(null, null, null), new ThreeWayMetric(null, null, null)),
        });

        md.Should().Contain("- 差分が大きい指標の要因考察: **未記入**");
    }

    // --- LLM 利用実績（月報 §7・#282 の出口） ---

    [Fact]
    public void 月報のLLM利用実績は未供給ならゼロ円と書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly));

        md.Should().Contain("**LLM の利用実績を照会できませんでした（供給元がありません）**");
        md.Should().Contain("「費用 0 円」「フォールバック 0 件」「スキップ 0 件」とは区別しています");
    }

    // 🔴 **取引判断の費用と報告書生成の費用を分けて出す**（#282 の是正の出口）。
    [Fact]
    public void 月報のLLM利用実績は取引判断と報告書生成の費用を分けて出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            LlmUsage = new LlmUsageRecord(
            [
                new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "m"),
                new LlmCostIncurred(450m, T0, LlmPurposes.ReportMonthly, "m"),
                new LlmCostIncurred(120m, T0, "information-collection", "m"),
            ],
            [new LlmFallbackFired("report-monthly", "a", "b", "FallbackFired", T0)],
            [new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0)]),
        });

        md.Should().Contain("取引判断の費用実績（月次上限 15,000 JPY に対する消費率） | +3,000 JPY / 20.0%");
        md.Should().Contain("報告書生成の費用実績（上限の対象外） | report-monthly: +450 JPY");
        md.Should().Contain("その他の用途の費用実績（上限の対象外） | +120 JPY");
        md.Should().Contain("フォールバック発火回数（用途別・原因別） | report-monthly／FallbackFired: 1 件");
        md.Should().Contain($"モデル利用不能による取引判断スキップ回数 | 1 件（{TradeDecisionSkipReasons.ModelUnavailable}: 1 件）");
    }

    // ADR-0017 決定2: スキップ 0 件は**障害ではない**と本文が明示する。
    [Fact]
    public void スキップゼロ件には設計上の正常な結果である旨を添える()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            LlmUsage = new LlmUsageRecord([], [], []),
        });

        md.Should().Contain("モデル利用不能による取引判断スキップ回数 | 0 件（**障害ではなく設計上の正常な結果である**。ADR-0017 決定2）");
    }

    // --- 🔴 縮退件数（INDEX 決定44） ---

    // **否定形**: 供給が無い縮退件数を 0 回 / 0 件と書かない。
    [Fact]
    public void 縮退件数は未供給ならゼロ回ゼロ件と書かない()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with { LlmUsage = new LlmUsageRecord([], [], []) });

        md.Should().Contain("スクリーニング入力の分割回数・切り詰め発生件数 | **供給されていません**");
        md.Should().Contain("**0 回 / 0 件ではありません**");
    }

    // **対の肯定形**: 供給されたら**分割と切り詰めを分けて**、削った対象の内訳とともに出る。
    [Fact]
    public void 縮退件数は分割と切り詰めを分けて内訳とともに出す()
    {
        var md = ReportRenderer.RenderMarkdown(View(ReportKind.Monthly) with
        {
            LlmUsage = new LlmUsageRecord([], [], [],
                new ScreeningDegradationCounts(4, 2, new Dictionary<string, int> { ["RAG"] = 1, ["ニュース"] = 1 })),
        });

        md.Should().Contain("スクリーニング入力の分割回数・切り詰め発生件数 | 分割 4 回 / 切り詰め 2 件（RAG: 1 / ニュース: 1）");
    }

    // --- 🔴 FR-16: LLM 非介在をアーキテクチャ上も担保する ---

    // 散文だけを差し替えた 2 つの描画で、**数値を含む節がすべて一致する**。
    // 一致しなければ、散文（LLM 出力）が数値の描画に影響している＝FR-16 の違反である。
    [Fact]
    public void 散文を差し替えても数値の節は一文字も変わらない()
    {
        var supplied = View(ReportKind.Monthly) with
        {
            LlmUsage = new LlmUsageRecord(
                [new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "m")], [], []),
            BorrowFees = new BorrowFeeRecord(
                [new BorrowFeeAccrued("AAPL", Market.UnitedStates, new DateOnly(2026, 8, 3), 0.06m, 10_000m, 1.64m, T0)], []),
            FxTranslation = new FxTranslationSummary(-1_234m, 5),
            Uptime = new OpenDUptimeRecord([new OpenDUptimeDay(new DateOnly(2026, 8, 3), 1.0m)], 41),
            ThreeWayComparison = new ThreeWayComparison(
                new ThreeWayMetric(0.55m, 0m, null), new ThreeWayMetric(12.5m, 0m, null),
                new ThreeWayMetric(0.08m, 0m, null), new ThreeWayMetric(120m, 0m, null)),
        };

        var a = ReportRenderer.RenderMarkdown(supplied with { Narrative = "散文 A" });
        var b = ReportRenderer.RenderMarkdown(supplied with { Narrative = "**まったく違う散文 B。損益は +99,999 USD だった。**" });

        // 散文節そのものは当然変わる。数値を持つ節（サマリ・統制・稼働率・三者比較・LLM 実績）は一致する。
        NumericSections(a).Should().Be(NumericSections(b));
        a.Should().NotBe(b); // 対の肯定形: 散文は確かに差し替わっている
    }

    // --- 🔴 #308 / #315: 縮退（プレースホルダ散文）でも数値節は完全に出る ---

    // 散文 LLM がタイムアウト等で縮退すると `PlaceholderReportNarrativeDrafter` の定型文になり、
    // そのとき **モデルは知り得ないため `LlmModelUsage` は null** である（IADR-0217 の規律）。
    //
    // 🔴 **否定形**: モデルの節は出さない（「第 1 候補で書かれた」と読ませない）。
    // 🔴 **対の肯定形**: それでも**数値節は 1 つ残らず出る**——散文の縮退が数値の欠落へ波及しない。
    [Fact]
    public void 縮退でプレースホルダ散文になっても数値節は完全に出る()
    {
        var degraded = View(ReportKind.Monthly) with
        {
            Narrative = "（LLM 未接続のため定型文）",
            LlmModelUsage = null, // 縮退時はモデルを知り得ない
            LlmUsage = new LlmUsageRecord(
                [new LlmCostIncurred(3_000m, T0, LlmPurposes.TradeDecision, "m")], [], []),
            BorrowFees = new BorrowFeeRecord([], []),
            FxTranslation = new FxTranslationSummary(-1_234m, 5),
            Uptime = new OpenDUptimeRecord([new OpenDUptimeDay(new DateOnly(2026, 8, 3), 1.0m)], 41),
        };

        var md = ReportRenderer.RenderMarkdown(degraded);

        // 否定形: モデルの節は出ない。
        md.Should().NotContain("### 散文生成に使用した LLM");

        // 対の肯定形: 数値節はすべて出る。
        md.Should().Contain("月間実現損益（税引後・費用込み） | +1,520.00 USD");
        md.Should().Contain("為替差損益（独立表示） | -1,234 JPY（明細 5 件）");
        md.Should().Contain("### 当月の統制作動状況");
        md.Should().Contain("## 5. 当月の OpenD 稼働率分布");
        md.Should().Contain("## 6. バックテスト / SIMULATE / 実弾の三者比較");
        md.Should().Contain("## 7. 当月の LLM 利用実績");
        md.Should().Contain("取引判断の費用実績（月次上限 15,000 JPY に対する消費率） | +3,000 JPY / 20.0%");
    }

    // 散文節（"## 2." 〜 "## 3." の直前）を除いた本文。数値はすべてこちら側にある。
    private static string NumericSections(string markdown)
    {
        var start = markdown.IndexOf("## 2.", StringComparison.Ordinal);
        var end = markdown.IndexOf("## 3.", StringComparison.Ordinal);
        return markdown[..start] + markdown[end..];
    }
}
