using AuditService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests;

// FR-04, FR-11, UC-01, UC-07, ADR-0017 決定2・決定4-(3), #335, #347, IADR-0216/0217/0219:
// LLM 統制まわりの 3 イベント（費用発生・フォールバック発火・取引判断の見送り）→ AuditEntry の写像。
//
// 🔴 **相関の切り方がこの写像の要点である。** これらのイベントは注文相関を持たない。
// 決定4-(3) は「③月報に当月のフォールバック発火回数を記載する」と定めており、**発生月で束ねられる相関**が
// 集計の前提になる。相関が 1 本に固定されていると全期間が混ざり、月ごとに数えられない。
public class AuditEntryFactoryLlmGovernanceTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset RecordedAt = new(2026, 8, 15, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset July = new(2026, 7, 20, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset August = new(2026, 8, 3, 1, 0, 0, TimeSpan.Zero);

    // ---- LlmCostIncurred（#347: 用途・モデルを要約へ出す） -------------------------------------

    // 🔴 **要約だけを走査する監査**で用途が読めなければ、上限の対象／対象外を後から切り分けられない。
    // 対象外（報告書生成）の費用は抑制されないまま台帳に積まれるため、区別が付くことが特に重要である。
    [Fact]
    public void LLM費用発生は用途とモデルを要約に残す()
    {
        var entry = AuditEntryFactory.From(
            new LlmCostIncurred(12.34m, July, LlmPurposes.ReportMonthly, "claude-opus-5"), Id, RecordedAt);

        entry.EventType.Should().Be(nameof(LlmCostIncurred));
        entry.Summary.Should().Contain("12.34");
        entry.Summary.Should().Contain(LlmPurposes.ReportMonthly);
        entry.Summary.Should().Contain("claude-opus-5");
        entry.OccurredAt.Should().Be(July);
        entry.RecordedAt.Should().Be(RecordedAt);
    }

    // 用途・モデルを持たない従来形（取引判断サービスの既存発行）でも写像は壊れず、
    // **空欄ではなく「不明」と書く**（読み手が「載っていない」と「無かった」を取り違えない）。
    [Fact]
    public void 用途とモデルが無い費用発生も不明と明記して記録する()
    {
        var entry = AuditEntryFactory.From(new LlmCostIncurred(1m, July), Id, RecordedAt);

        entry.Summary.Should().Contain("不明");
    }

    // 月次集計の供給元であるため、相関は発生月で束ねる。
    [Fact]
    public void LLM費用発生の相関は発生月ごとに分かれる()
    {
        var july = AuditEntryFactory.From(new LlmCostIncurred(1m, July), Id, RecordedAt);
        var alsoJuly = AuditEntryFactory.From(new LlmCostIncurred(2m, July.AddDays(5)), Id, RecordedAt);
        var august = AuditEntryFactory.From(new LlmCostIncurred(3m, August), Id, RecordedAt);

        alsoJuly.CorrelationId.Should().Be(july.CorrelationId, "同月の計上は 1 本の相関で辿れること");
        august.CorrelationId.Should().NotBe(july.CorrelationId, "月をまたぐと別の相関になること");
    }

    // ---- LlmFallbackFired（ADR-0017 決定4-(3): 月報の発火回数の供給元） -------------------------

    [Fact]
    public void フォールバック発火は用途と期待_実効モデルを要約に残す()
    {
        var entry = AuditEntryFactory.From(
            new LlmFallbackFired(
                LlmPurposes.ReportMonthly, LlmAssignments.Opus5, "claude-sonnet-5",
                nameof(LlmAssignmentOutcome.FallbackFired), July),
            Id, RecordedAt);

        entry.EventType.Should().Be(nameof(LlmFallbackFired));
        entry.Summary.Should().Contain(LlmPurposes.ReportMonthly);
        entry.Summary.Should().Contain(LlmAssignments.Opus5);
        entry.Summary.Should().Contain("claude-sonnet-5");
        entry.Summary.Should().Contain(nameof(LlmAssignmentOutcome.FallbackFired));
        entry.OccurredAt.Should().Be(July);
        // 全量 JSON は台帳の権威源（FR-11）。要約の切り詰めで原因が失われても Detail から復元できる。
        entry.Detail.Should().Contain(LlmPurposes.ReportMonthly);
    }

    // 🔴 決定4-(3) の「**当月の**発火回数」は、相関が月で分かれていて初めて数えられる。
    [Fact]
    public void フォールバック発火の相関は発生月ごとに分かれる()
    {
        var july = AuditEntryFactory.From(
            new LlmFallbackFired(LlmPurposes.ReportDaily, LlmAssignments.Sonnet5, LlmAssignments.Haiku45,
                nameof(LlmAssignmentOutcome.FallbackFired), July), Id, RecordedAt);
        var august = AuditEntryFactory.From(
            new LlmFallbackFired(LlmPurposes.ReportDaily, LlmAssignments.Sonnet5, LlmAssignments.Haiku45,
                nameof(LlmAssignmentOutcome.FallbackFired), August), Id, RecordedAt);

        august.CorrelationId.Should().NotBe(july.CorrelationId);
    }

    // 期待・実効のいずれかが不明でも「なし」「不明」と明記する（空欄で流さない）。
    [Fact]
    public void 割当や実効モデルが不明でも明記して記録する()
    {
        var entry = AuditEntryFactory.From(
            new LlmFallbackFired("unknown-purpose", null, null, nameof(LlmAssignmentOutcome.Unassigned), July),
            Id, RecordedAt);

        entry.Summary.Should().Contain("なし");
        entry.Summary.Should().Contain("不明");
    }

    // ---- TradeDecisionSkipped（ADR-0017 決定2: 見送りは正常な結果） ----------------------------

    // 🔴 **沈黙のスキップにしない**（決定2）。台帳に残らなければ「なぜ発注が無かったのか」を後から辿れず、
    // 「相場を見て見送った」と「モデルが使えず実行できなかった」が区別できなくなる。
    [Fact]
    public void 取引判断の見送りは理由と期待モデルを要約に残す()
    {
        var entry = AuditEntryFactory.From(
            new TradeDecisionSkipped(
                LlmPurposes.TradeDecision, nameof(LlmAssignmentOutcome.Unassigned),
                LlmAssignments.Sonnet5, "claude-haiku-4-5", July),
            Id, RecordedAt);

        entry.EventType.Should().Be(nameof(TradeDecisionSkipped));
        entry.Summary.Should().Contain("見送り");
        entry.Summary.Should().Contain(LlmPurposes.TradeDecision);
        entry.Summary.Should().Contain(LlmAssignments.Sonnet5);
        entry.Summary.Should().Contain(nameof(LlmAssignmentOutcome.Unassigned));
        entry.OccurredAt.Should().Be(July);
    }

    // 日報は「当日のスキップ回数」を書くため日で絞って数える。相関は発火と同じく月で束ねる。
    [Fact]
    public void 取引判断の見送りの相関は発生月ごとに分かれる()
    {
        var july = AuditEntryFactory.From(
            new TradeDecisionSkipped(LlmPurposes.TradeDecision, "Unassigned", LlmAssignments.Sonnet5, null, July),
            Id, RecordedAt);
        var august = AuditEntryFactory.From(
            new TradeDecisionSkipped(LlmPurposes.TradeDecision, "Unassigned", LlmAssignments.Sonnet5, null, August),
            Id, RecordedAt);

        august.CorrelationId.Should().NotBe(july.CorrelationId);
    }

    // 🔴 **見送りと発火は別の相関に置く。** 同じ相関へ混ぜると、月報の「発火回数」に見送りが混入して数が狂う。
    [Fact]
    public void 見送りとフォールバック発火は同月でも別の相関になる()
    {
        var skipped = AuditEntryFactory.From(
            new TradeDecisionSkipped(LlmPurposes.TradeDecision, "Unassigned", LlmAssignments.Sonnet5, null, July),
            Id, RecordedAt);
        var fired = AuditEntryFactory.From(
            new LlmFallbackFired(LlmPurposes.ReportDaily, LlmAssignments.Sonnet5, LlmAssignments.Haiku45,
                nameof(LlmAssignmentOutcome.FallbackFired), July), Id, RecordedAt);

        skipped.CorrelationId.Should().NotBe(fired.CorrelationId);
    }
}
