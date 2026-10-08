using AuditService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests;

// FR-04, FR-09, FR-11, #1267, IADR-0517: LLM ゲートウェイの Sent=false の連続と回復の台帳の記録（T-04-018）。
// 取引判断の Hold（TradeDecisionHeld）は根拠を運ばない（IADR-0452 決定5）ため、「なぜ LLM なしの Hold が続いたか」の
// 台帳上の証跡はこの 2 行である。
public class LlmGatewayUnsentAuditEntryTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 18, 11, 11, TimeSpan.Zero);

    // T-04-018: 要約は原因をゲートウェイの申告のまま書き（機密区分と書かない）、本文に全項目が載る。
    [Fact]
    public void T_04_018_送信不可の連続は申告のまま要約へ残す()
    {
        var e = new LlmGatewayUnsentDetected(
            "trade-decision-screening", 5, "UpstreamError", 429, "internal は anthropic-managed へ送信可",
            "呼び出し先 anthropic-managed が現在利用できません。", T0, T0.AddMinutes(4));

        var entry = AuditEntryFactory.From(e, Id, T0.AddMinutes(5));

        entry.EventType.Should().Be(nameof(LlmGatewayUnsentDetected));
        entry.Symbol.Should().BeNull();
        entry.OccurredAt.Should().Be(e.OccurredAt);
        entry.Summary.Should().Contain("5 回連続").And.Contain("上流の不調").And.Contain("上流 429")
            .And.Contain("trade-decision-screening");
        entry.Summary.Should().NotContain("機密区分");
        entry.Detail.Should().Contain("UpstreamError").And.Contain("anthropic-managed");
    }

    // T-04-018: 連続と回復は同じ相関（期間を 1 本で辿れる）。別の連続は別の相関。
    [Fact]
    public void T_04_018_連続と回復は同じ相関で_別の連続とは分ける()
    {
        var detected = AuditEntryFactory.From(
            new LlmGatewayUnsentDetected("trade-decision", 5, null, null, null, null, T0, T0.AddMinutes(4)), Id, T0);
        var recovered = AuditEntryFactory.From(new LlmGatewayUnsentRecovered(132, T0, T0.AddMinutes(109)), Id, T0);
        var other = AuditEntryFactory.From(
            new LlmGatewayUnsentDetected("trade-decision", 5, null, null, null, null, T0.AddDays(1), T0.AddDays(1)), Id, T0);

        recovered.CorrelationId.Should().Be(detected.CorrelationId);
        // T-04-022: 内訳があれば要約へ載せる（連続は用途を分けずに数える）。
        AuditEntryFactory.From(
            new LlmGatewayUnsentRecovered(6, T0, T0.AddMinutes(30),
                new Dictionary<string, int> { ["trade-decision-screening"] = 4, ["trade-decision"] = 2 }), Id, T0)
            .Summary.Should().Contain("内訳 trade-decision 2・trade-decision-screening 4");
        other.CorrelationId.Should().NotBe(detected.CorrelationId);
        recovered.Summary.Should().Contain("1.8 時間").And.Contain("132 件");
        detected.Summary.Should().Contain("種別不明").And.Contain("（ゲートウェイの申告なし）");
    }
}
