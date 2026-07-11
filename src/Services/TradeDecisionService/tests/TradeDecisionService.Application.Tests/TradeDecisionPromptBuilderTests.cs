using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.Services;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.TradeDecision.Application.Tests;

// FR-02, FR-04, IADR-0023: プロンプトのトリガー種別分岐（定時/価格変動）の出力を検証する。
public class TradeDecisionPromptBuilderTests
{
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 7, 10), "米国株の押し目買い方針");
    private static readonly SizingContext Context =
        new(100_000m, 50_000m, 20_000m, 0, 0m, TradeMode.Paper, TradingDefaults.CreateRiskLimits());

    [Fact]
    public void 価格変動トリガーは価格変動セクションを出力する()
    {
        var trigger = DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, DateTimeOffset.UtcNow));

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context);

        prompt.Should().Contain("価格変動トリガー");
        prompt.Should().Contain("現在値");
        prompt.Should().Contain("AAPL");
        prompt.Should().NotContain("定時サイクル");
    }

    [Fact]
    public void 定時トリガーは定時セクションを出力し価格行を含まない()
    {
        var trigger = DecisionTrigger.Scheduled("7203", Market.Japan);

        var prompt = TradeDecisionPromptBuilder.Build(trigger, Policy, Context);

        prompt.Should().Contain("定時サイクル");
        prompt.Should().Contain("7203");
        // 定時セクションは価格データ行（現在値/基準値/変動率）を含まない。
        prompt.Should().NotContain("現在値");
    }

    // IADR-0039, L129: 一次スクリーニング用プロンプトは絞り込みに徹し、本判断と同じ JSON スキーマを再利用する。
    [Fact]
    public void スクリーニングプロンプトは絞り込み文言と共通JSONスキーマを出力する()
    {
        var trigger = DecisionTrigger.Scheduled("AAPL", Market.UnitedStates);

        var prompt = TradeDecisionPromptBuilder.BuildScreening(trigger, Policy, Context);

        prompt.Should().Contain("一次スクリーニング");
        prompt.Should().Contain("絞り込");
        prompt.Should().Contain("AAPL");
        prompt.Should().Contain(Policy.Summary);
        // Parser 共有のため本判断と同じ action スキーマ（Buy|Sell|Hold）を要求する。
        prompt.Should().Contain("Buy|Sell|Hold");
    }
}
