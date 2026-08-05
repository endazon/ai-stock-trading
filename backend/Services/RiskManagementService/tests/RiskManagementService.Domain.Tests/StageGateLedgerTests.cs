using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.RiskManagement.Domain.Tests;

// FR-20, IADR-0041: 遷移履歴の畳み込み。現在段階を append-only 履歴の fold で導出し、遷移履歴が監査できる。
public class StageGateLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 9, 0, 0, TimeSpan.Zero);

    private static StageTransition Promotion(int seq, TradingStage from, TradingStage to) =>
        new(seq, from, to, StageTransitionKind.Promotion, "endazon", Now, "利用者承認による昇格");

    // FR-20: 空の台帳は初期段階・次シーケンス 1・履歴なし
    [Fact]
    public void 空の台帳は初期段階と次シーケンス1を返す()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification);

        ledger.CurrentStage.Should().Be(TradingStage.Stage0Verification);
        ledger.NextSequence.Should().Be(1);
        ledger.History.Should().BeEmpty();
    }

    // FR-20: 追記すると現在段階が遷移先へ進み、次シーケンスが増え、履歴が残る（監査可能）
    [Fact]
    public void 追記で現在段階と次シーケンスが更新され履歴が残る()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification)
            .Append(Promotion(1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate));

        ledger.CurrentStage.Should().Be(TradingStage.Stage1Simulate);
        ledger.NextSequence.Should().Be(2);
        ledger.History.Should().HaveCount(1);
        ledger.History[0].ApprovedBy.Should().Be("endazon");
    }

    // FR-20: 複数遷移の畳み込みで現在段階は最後の遷移先になる
    [Fact]
    public void 複数遷移の畳み込みで現在段階は最後の遷移先になる()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification)
            .Append(Promotion(1, TradingStage.Stage0Verification, TradingStage.Stage1Simulate))
            .Append(Promotion(2, TradingStage.Stage1Simulate, TradingStage.Stage2MinimalLive));

        ledger.CurrentStage.Should().Be(TradingStage.Stage2MinimalLive);
        ledger.NextSequence.Should().Be(3);
        ledger.History.Should().HaveCount(2);
    }

    // FR-20: 追記整合。遷移元が現在段階と不一致なら例外（履歴の一貫性を保証）
    [Fact]
    public void 遷移元が現在段階と不一致なら例外()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification);

        var act = () => ledger.Append(Promotion(1, TradingStage.Stage1Simulate, TradingStage.Stage2MinimalLive));

        act.Should().Throw<InvalidOperationException>();
    }

    // FR-20: 追記整合。シーケンスが連番でないなら例外（append-only ログの順序を保証）
    [Fact]
    public void シーケンスが連番でないなら例外()
    {
        var ledger = StageGateLedger.Empty(TradingStage.Stage0Verification);

        var act = () => ledger.Append(Promotion(2, TradingStage.Stage0Verification, TradingStage.Stage1Simulate));

        act.Should().Throw<InvalidOperationException>();
    }
}
