using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// FR-04, FR-06, ADR-0014, ADR-0015, ADR-0017, #335, IADR-0215:
// 用途別割当表とフォールバック順序の**スナップショットテスト**（計画の確定値で固定する）。
//
// 🔴 本テストは「実装が表を読めること」ではなく「**表の値が計画と一致していること**」を守る。
// したがって期待値は表から組み立てず、**計画の条文からリテラルで書き写す**（表から組み立てると
// トートロジーになり、値が変わっても緑のまま通る）。
public class LlmAssignmentsTests
{
    // ADR-0014 §決定1 の 2026-08-01 改訂表 ＋ ADR-0015 §決定（月報）＋ ADR-0017 決定1・決定2 ＋
    // 01_architecture-overview §判断の二段化（スクリーニング層）。
    [Fact]
    public void 割当表は計画の確定値と一致する()
    {
        var snapshot = LlmAssignments.All
            .Select(a => $"{a.Purpose} | {a.PrimaryModel} | [{string.Join(", ", a.FallbackModels)}] | fallback={a.FallbackAllowed}")
            .ToArray();

        snapshot.Should().Equal(
            "trade-decision | claude-sonnet-5 | [] | fallback=False",
            "trade-decision-screening | claude-haiku-4-5 | [] | fallback=False",
            "report-monthly | claude-opus-5 | [claude-sonnet-5] | fallback=True",
            "report-weekly | claude-opus-5 | [claude-sonnet-5] | fallback=True",
            "report-daily | claude-sonnet-5 | [claude-haiku-4-5] | fallback=True");
    }

    // ADR-0017 決定1 の明文: 「**すべての用途でフォールバックが安価側へ向かう表である。**
    // 費用が上がる遷移は本表に存在しない。」——第 1 候補と同じモデルへ落ちる鎖も存在しない。
    [Fact]
    public void フォールバック先は第1候補と重複しない_同じモデルへ落ちる鎖を作らない()
    {
        foreach (var a in LlmAssignments.All)
            a.FallbackModels.Should().NotContain(a.PrimaryModel, $"{a.Purpose} の鎖が第 1 候補へ戻っている");
    }

    // 🔴 **否定形**（#335 の受け入れ基準）: ADR-0015 / ADR-0017 決定1 により
    // `claude-fable-5` は本システムで使用しない。第 1 候補にも第 2 候補にも現れてはならない。
    [Fact]
    public void claude_fable_5_はどの用途の第1候補にも第2候補にも現れない()
    {
        var everyModel = LlmAssignments.All
            .SelectMany(a => a.FallbackModels.Prepend(a.PrimaryModel))
            .ToArray();

        everyModel.Should().NotBeEmpty();
        everyModel.Should().NotContain(LlmAssignments.ForbiddenModel);
    }

    // ADR-0017 決定2: 取引判断（本判断・スクリーニング）は**いかなる理由でもフォールバックしない**。
    // 鎖が空であることと、`FallbackAllowed=false` であることの**両方**を固定する
    // （鎖だけを見ると「たまたま空」と区別できず、後から鎖を足す変更が統制の逸脱だと気づけない）。
    [Theory]
    [InlineData(LlmPurposes.TradeDecision)]
    [InlineData(LlmPurposes.TradeDecisionScreening)]
    public void 取引判断系はフォールバックを禁止する(string purpose)
    {
        var assignment = LlmAssignments.For(purpose)!;

        assignment.FallbackAllowed.Should().BeFalse();
        assignment.FallbackModels.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LlmPurposes.ReportMonthly)]
    [InlineData(LlmPurposes.ReportWeekly)]
    [InlineData(LlmPurposes.ReportDaily)]
    public void 報告書はフォールバックを許す(string purpose) =>
        LlmAssignments.For(purpose)!.FallbackAllowed.Should().BeTrue();

    [Fact]
    public void 用途キーの大小は無視する() =>
        LlmAssignments.For("TRADE-DECISION")!.PrimaryModel.Should().Be("claude-sonnet-5");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rag-answer")] // 基盤側の用途（本 ADR の対象外）
    public void 未登録の用途は割当を持たない(string? purpose) =>
        LlmAssignments.For(purpose).Should().BeNull();

    // ---- 実効モデルの評価（境界値テーブル） -------------------------------------------------

    // 取引判断: ピンのみ Allowed。フォールバック先（表に無い）・DefaultModel 落ち・禁止モデルはすべて不可。
    [Theory]
    // 用途 / 実効モデル / 期待 Outcome / 期待 Allowed
    [InlineData(LlmPurposes.TradeDecision, "claude-sonnet-5", LlmAssignmentOutcome.Primary, true)]
    [InlineData(LlmPurposes.TradeDecision, "CLAUDE-SONNET-5", LlmAssignmentOutcome.Primary, true)]
    [InlineData(LlmPurposes.TradeDecision, " claude-sonnet-5 ", LlmAssignmentOutcome.Primary, true)]
    [InlineData(LlmPurposes.TradeDecision, "claude-opus-5", LlmAssignmentOutcome.Unassigned, false)]
    [InlineData(LlmPurposes.TradeDecision, "claude-haiku-4-5", LlmAssignmentOutcome.Unassigned, false)]
    [InlineData(LlmPurposes.TradeDecision, "claude-fable-5", LlmAssignmentOutcome.Forbidden, false)]
    [InlineData(LlmPurposes.TradeDecision, null, LlmAssignmentOutcome.Unassigned, false)]
    [InlineData(LlmPurposes.TradeDecisionScreening, "claude-haiku-4-5", LlmAssignmentOutcome.Primary, true)]
    [InlineData(LlmPurposes.TradeDecisionScreening, "claude-sonnet-5", LlmAssignmentOutcome.Unassigned, false)]
    // 報告書: 第 2 候補も Allowed（FallbackFired として記録はする）。
    [InlineData(LlmPurposes.ReportMonthly, "claude-opus-5", LlmAssignmentOutcome.Primary, true)]
    [InlineData(LlmPurposes.ReportMonthly, "claude-sonnet-5", LlmAssignmentOutcome.FallbackFired, true)]
    [InlineData(LlmPurposes.ReportMonthly, "claude-haiku-4-5", LlmAssignmentOutcome.Unassigned, false)]
    [InlineData(LlmPurposes.ReportMonthly, "claude-fable-5", LlmAssignmentOutcome.Forbidden, false)]
    [InlineData(LlmPurposes.ReportDaily, "claude-sonnet-5", LlmAssignmentOutcome.Primary, true)]
    [InlineData(LlmPurposes.ReportDaily, "claude-haiku-4-5", LlmAssignmentOutcome.FallbackFired, true)]
    // 未登録の用途（基盤で DefaultModel へ落ちた形）はモデルによらず不可。
    [InlineData("unknown-purpose", "claude-opus-5", LlmAssignmentOutcome.Unassigned, false)]
    public void 実効モデルの評価は用途ごとの許否を返す(
        string purpose, string? effectiveModel, LlmAssignmentOutcome expectedOutcome, bool expectedAllowed)
    {
        var evaluation = LlmAssignmentEvaluator.Evaluate(purpose, effectiveModel);

        evaluation.Outcome.Should().Be(expectedOutcome);
        evaluation.Allowed.Should().Be(expectedAllowed);
    }

    // 🔴 **プロパティベース**（統制系 3 点セット）: 表に載るどのモデルを取っても、
    // 取引判断系で Allowed になるのは第 1 候補ただ 1 つである（ADR-0017 決定2）。
    [Fact]
    public void 取引判断系で許可される実効モデルは第1候補ただ1つである()
    {
        var everyKnownModel = LlmAssignments.All
            .SelectMany(a => a.FallbackModels.Prepend(a.PrimaryModel))
            .Append(LlmAssignments.ForbiddenModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var purpose in new[] { LlmPurposes.TradeDecision, LlmPurposes.TradeDecisionScreening })
        {
            var pin = LlmAssignments.For(purpose)!.PrimaryModel;
            var allowed = everyKnownModel
                .Where(m => LlmAssignmentEvaluator.Evaluate(purpose, m).Allowed)
                .ToArray();

            allowed.Should().Equal(pin);
        }
    }

    // 禁止モデルは**用途によらず**常に Forbidden（未登録の用途でも Unassigned へ潰さない）。
    // 「未知だった」と「使わないと決めていた」は別の運用事実である。
    [Theory]
    [InlineData(LlmPurposes.TradeDecision)]
    [InlineData(LlmPurposes.ReportMonthly)]
    [InlineData("unknown-purpose")]
    public void 禁止モデルは用途によらず_Forbidden_になる(string purpose) =>
        LlmAssignmentEvaluator.Evaluate(purpose, "claude-fable-5").Outcome
            .Should().Be(LlmAssignmentOutcome.Forbidden);
}
