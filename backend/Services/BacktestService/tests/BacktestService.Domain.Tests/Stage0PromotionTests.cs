using AiStockTrading.RiskManagement.Domain;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Backtest.Domain.Tests;

// FR-15, FR-20, ADR-0008: Stage 0 合格→Stage 1 昇格推奨（承認は利用者・#20）を検証する。
public class Stage0PromotionTests
{
    [Fact]
    public void 合格ならStage0からStage1への昇格を推奨する()
    {
        var gate = new Stage0GateResult(Passed: true, FailedChecks: []);
        var rec = Stage0Promotion.Evaluate(gate);

        rec.Recommended.Should().BeTrue();
        rec.FromStage.Should().Be(TradingStage.Stage0Verification);
        rec.ToStage.Should().Be(TradingStage.Stage1Paper);
    }

    [Fact]
    public void 不合格なら昇格を推奨しない_据え置き()
    {
        var gate = new Stage0GateResult(Passed: false, FailedChecks: [Stage0GateCheck.DeflatedSharpe]);
        var rec = Stage0Promotion.Evaluate(gate);

        rec.Recommended.Should().BeFalse();
        rec.ToStage.Should().BeNull();
        rec.FromStage.Should().Be(TradingStage.Stage0Verification);
    }
}
