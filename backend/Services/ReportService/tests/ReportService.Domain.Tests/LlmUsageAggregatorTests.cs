using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Report.Domain.Tests;

// FR-06, FR-16, #338, #282, #347, ADR-0017 決定2・決定4, 04_report-templates 月報 §7, IADR-0251:
// LLM 利用実績の集計（純関数）を固定する。
//
// 🔴 計画の明文: 「**取引判断の費用と報告書生成の費用は必ず分けて記載する**（合算すると、
// どちらが上限に効いているか分からなくなる）」。分別のずれは月次上限の運用そのものを壊すため、
// **境界値テーブル ＋ 否定形 ＋ 対の肯定形**で固定する。
public class LlmUsageAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 5, 3, 0, 0, TimeSpan.Zero);

    private static LlmUsageRecord Record(
        IReadOnlyList<LlmCostIncurred>? costs = null,
        IReadOnlyList<LlmFallbackFired>? fallbacks = null,
        IReadOnlyList<TradeDecisionSkipped>? skips = null,
        ScreeningDegradationCounts? degradation = null) =>
        new(costs ?? [], fallbacks ?? [], skips ?? [], degradation);

    // --- 費用の分別（境界値テーブル） ---

    // 🔴 用途ごとの倒し先。`LlmCostScope.IsGoverned` と**同じ分別**でなければならない。
    [Theory]
    // 取引判断（本判断・スクリーニング）は上限の対象。
    [InlineData(LlmPurposes.TradeDecision, true, false)]
    [InlineData(LlmPurposes.TradeDecisionScreening, true, false)]
    // 報告書生成は上限の対象外だが**計上はする**（#282 の是正点）。
    [InlineData(LlmPurposes.ReportMonthly, false, true)]
    [InlineData(LlmPurposes.ReportWeekly, false, true)]
    [InlineData(LlmPurposes.ReportDaily, false, true)]
    // 用途なし（従来の形）は**上限の対象へ倒す**（過小計上を作らない・IADR-0218）。
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    public void 用途ごとに費用の帰属先が決まる(string? purpose, bool governed, bool report)
    {
        var u = LlmUsageAggregator.Aggregate(Record(costs: [new LlmCostIncurred(100m, T0, purpose, "m")]));

        u.TradeDecisionCostJpy.Should().Be(governed ? 100m : 0m);
        u.ReportCostJpyByPurpose.Sum(e => e.AmountJpy).Should().Be(report ? 100m : 0m);
        // 上限の対象でも報告書でもない用途だけが「その他」へ入る。
        u.OtherCostJpy.Should().Be(governed || report ? 0m : 100m);
    }

    // 🔴 **否定形**: 報告書生成の費用が取引判断の費用へ混ざらない（混ざると月次上限が誤って効く）。
    // **対の肯定形**: 同じ入力で報告書側には確かに載る。
    [Fact]
    public void 報告書生成の費用は上限の対象へ積まれず報告書欄へ載る()
    {
        var u = LlmUsageAggregator.Aggregate(Record(costs:
        [
            new LlmCostIncurred(500m, T0, LlmPurposes.ReportMonthly, "m"),
            new LlmCostIncurred(300m, T0, LlmPurposes.TradeDecision, "m"),
        ]));

        u.TradeDecisionCostJpy.Should().Be(300m); // 否定形: 500 が混ざっていない
        u.ReportCostJpyByPurpose.Should().ContainSingle()
            .Which.Should().Be((LlmPurposes.ReportMonthly, 500m)); // 肯定形: 確かに載る
    }

    // 未知の用途（情報収集など）を**落とさない**。落とすと「どこにも現れない費用」ができ #282 と同じ形になる。
    [Fact]
    public void 上限の対象でも報告書でもない用途はその他へ計上する()
    {
        var u = LlmUsageAggregator.Aggregate(Record(costs:
            [new LlmCostIncurred(42m, T0, "information-collection", "m")]));

        u.OtherCostJpy.Should().Be(42m);
        u.TradeDecisionCostJpy.Should().Be(0m);
        u.ReportCostJpyByPurpose.Should().BeEmpty();
    }

    // 報告書生成の費用は**種別ごとに分けて**出す（計画 §7「月報 / 週報 / 日報」）。
    [Fact]
    public void 報告書生成の費用は用途別に集計し名前順で返す()
    {
        var u = LlmUsageAggregator.Aggregate(Record(costs:
        [
            new LlmCostIncurred(30m, T0, LlmPurposes.ReportWeekly, "m"),
            new LlmCostIncurred(10m, T0, LlmPurposes.ReportDaily, "m"),
            new LlmCostIncurred(20m, T0, LlmPurposes.ReportDaily, "m"),
        ]));

        u.ReportCostJpyByPurpose.Should().Equal(
            (LlmPurposes.ReportDaily, 30m),
            (LlmPurposes.ReportWeekly, 30m));
    }

    // --- 消費率（境界値） ---

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(7500, 0.5)]
    [InlineData(15000, 1.0)]   // 上限ちょうど
    [InlineData(15001, 1.0000666666666666666666666667)] // 超過しても抑制はしない（検知のみ）
    public void 消費率は月次上限に対する比である(decimal amount, decimal expected)
    {
        LlmUsageAggregator.ConsumptionRatio(amount).Should().BeApproximately(expected, 0.000001m);
    }

    // 🔴 上限が 0 のとき「0%」と書かない（算出不能である）。0 を返すと「まだ使っていない」と読める。
    [Fact]
    public void 上限がゼロなら消費率は算出できない()
    {
        LlmUsageAggregator.ConsumptionRatio(100m, 0m).Should().BeNull();
    }

    // 🔴 消費率の分母は**前提条件の月次 LLM 上限そのもの**でなければならない。
    // 値を書き写すと、前提条件だけが改定されたときに分母が古いまま残り、
    // **計算は成立するので誰も気づかないまま消費率が誤表示になる**（危険側は過小表示）。
    // 本テストは、既定値を literal へ戻す変更を落とすための番人である。
    [Fact]
    public void 消費率の分母は前提条件の月次LLM上限と同一である()
    {
        LlmUsageAggregator.MonthlyLlmCostLimitJpy
            .Should().Be(TradingAssumptionsDefaults.Create().CostLimits.Llm);
    }

    // 対の肯定形: 上限を明示すればその値が分母になる（既定へ落ちない）。
    [Fact]
    public void 上限を明示すればその値が分母になる()
    {
        LlmUsageAggregator.ConsumptionRatio(5_000m, 10_000m).Should().Be(0.5m);
    }

    // --- フォールバック・スキップ ---

    [Fact]
    public void フォールバックは用途と原因の組で数える()
    {
        var u = LlmUsageAggregator.Aggregate(Record(fallbacks:
        [
            new LlmFallbackFired("report-monthly", "a", "b", "FallbackFired", T0),
            new LlmFallbackFired("report-monthly", "a", "b", "FallbackFired", T0),
            new LlmFallbackFired("report-daily", "a", null, "Unassigned", T0),
        ]));

        u.FallbacksByPurposeAndOutcome.Should().Equal(
            ("report-daily", "Unassigned", 1),
            ("report-monthly", "FallbackFired", 2));
    }

    [Fact]
    public void 取引判断のスキップは事由別に数える()
    {
        var u = LlmUsageAggregator.Aggregate(Record(skips:
        [
            new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0),
            new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelMismatch, "a", "b", T0),
            new TradeDecisionSkipped("trade-decision", TradeDecisionSkipReasons.ModelUnavailable, "a", null, T0),
        ]));

        u.SkipCount.Should().Be(3);
        u.SkipsByReason.Should().Equal(
            (TradeDecisionSkipReasons.ModelMismatch, 1),
            (TradeDecisionSkipReasons.ModelUnavailable, 2));
    }

    // 集計は決定的である（入力順に依存しない）。同じ多重集合なら同じ結果になる。
    [Fact]
    public void 集計は入力順に依存しない()
    {
        LlmCostIncurred[] costs =
        [
            new(10m, T0, LlmPurposes.ReportDaily, "m"),
            new(20m, T0, LlmPurposes.TradeDecision, "m"),
            new(30m, T0, LlmPurposes.ReportWeekly, "m"),
        ];

        var forward = LlmUsageAggregator.Aggregate(Record(costs: costs));
        var reversed = LlmUsageAggregator.Aggregate(Record(costs: [.. costs.Reverse()]));

        // record の既定等価は列を参照で比べるため、値で突き合わせる。
        reversed.TradeDecisionCostJpy.Should().Be(forward.TradeDecisionCostJpy);
        reversed.OtherCostJpy.Should().Be(forward.OtherCostJpy);
        reversed.ReportCostJpyByPurpose.Should().Equal(forward.ReportCostJpyByPurpose);
        reversed.SkipsByReason.Should().Equal(forward.SkipsByReason);
        reversed.FallbacksByPurposeAndOutcome.Should().Equal(forward.FallbacksByPurposeAndOutcome);
    }

    // 🔴 縮退件数は**素通しする**。集計器が 0 を発明しない（未供給と 0 の区別は描画側で表れる）。
    [Fact]
    public void 縮退件数は供給された値をそのまま保持する()
    {
        var counts = new ScreeningDegradationCounts(3, 2, new Dictionary<string, int> { ["RAG"] = 1, ["ニュース"] = 1 });

        Record(degradation: counts).ScreeningDegradation.Should().Be(counts);
        Record().ScreeningDegradation.Should().BeNull();
    }
}
