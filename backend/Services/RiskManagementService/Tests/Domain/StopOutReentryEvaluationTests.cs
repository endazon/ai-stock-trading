using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// FR-10, #935, IADR-0394: 判定コア（RiskEvaluator）が「当日の損切り」の供給で新規建てを止めること。
// 供給の組み立て（どの決済を損切りと数えるか・当日の判定）は StopOutProjectionTests、
// 台帳 → 審査の通しは StopOutReentryRegressionTests、Program.cs の実構成は StopOutReentryWiringTests が見る。
public class StopOutReentryEvaluationTests
{
    private static OrderIntent Intent(TradeSide side, PositionEffect effect, ProductType productType = ProductType.Cash) =>
        new("AAPL", Market.UnitedStates, side, productType, BrokerProvider.InternalPaper, 10, 300m, effect);

    // 金額系の上限の内側（注文額 3,000 は equity 100,000 の 25% の内側）。ほかの理由を混ぜない。
    private static PortfolioSnapshot Snapshot() => new() { Capital = 100_000m };

    // ショート建て（売りの新規建て）を検証するため、空売りを有効にした設定（Stage 3 相当・資金上限は実質無制限）。
    private static RiskManagementSettings ShortEnabledSettings() => TradingDefaults.CreateSettings() with
    {
        Guard = TradingDefaults.CreateGuardSettings() with
        {
            EnabledProductTypes = new HashSet<ProductType> { ProductType.Cash, ProductType.MarginLong, ProductType.ShortSell },
        },
        Stage = new StageSettings(TradingStage.Stage3ScaledLive, BrokerProvider.InternalPaper, CapitalCapRatio: 1_000_000m),
    };

    private static OrderScreeningResult Evaluate(OrderIntent intent, StopOutReentrySupply? stopOuts) =>
        RiskEvaluator.Evaluate(intent, TradingDefaults.CreateSettings(), Snapshot(), stopOuts: stopOuts);

    // T-10-770: ロングを損切りした当日は、買いの新規建てを StoppedOutSameDay で止める。
    [Fact]
    public void ロングを損切りした当日の買いの新規建ては拒否される()
    {
        var supply = new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None);

        var result = Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), supply);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().ContainSingle().Which.Should().Be(RejectionReason.StoppedOutSameDay);
    }

    // T-10-770: 裁定は「同じ方向」に限る。ロングの損切りは売りの新規建て（ショート）を止めない。
    [Fact]
    public void ロングの損切りは反対方向の新規建てを止めない()
    {
        var supply = new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None);
        var shortEntry = Intent(TradeSide.Sell, PositionEffect.Open, ProductType.ShortSell);

        var result = RiskEvaluator.Evaluate(shortEntry, ShortEnabledSettings(), Snapshot(), stopOuts: supply);

        result.Reasons.Should().NotContain(RejectionReason.StoppedOutSameDay);
        result.Reasons.Should().NotContain(RejectionReason.StopOutStatusUnknown);
    }

    // T-10-770: ショートを損切りした当日は、売りの新規建て（ショート）を止め、買いの新規建ては止めない。
    [Fact]
    public void ショートを損切りした当日は売りの新規建てだけを止める()
    {
        var supply = new StopOutReentrySupply(StopOutStatus.None, StopOutStatus.StoppedOut);
        var shortEntry = Intent(TradeSide.Sell, PositionEffect.Open, ProductType.ShortSell);

        RiskEvaluator.Evaluate(shortEntry, ShortEnabledSettings(), Snapshot(), stopOuts: supply)
            .Reasons.Should().Contain(RejectionReason.StoppedOutSameDay);
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), supply)
            .IsApproved.Should().BeTrue();
    }

    // T-10-773: 🔴 不明は止める。「分からない」を「損切りしていない」として通さない（別の名前の理由で止める）。
    [Fact]
    public void 損切りしたか分からない当日の同方向の新規建ては不明の理由で拒否される()
    {
        var supply = new StopOutReentrySupply(StopOutStatus.Unknown, StopOutStatus.None);

        var result = Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), supply);

        result.IsApproved.Should().BeFalse();
        result.Reasons.Should().ContainSingle().Which.Should().Be(RejectionReason.StopOutStatusUnknown);
    }

    // T-10-773: 無しと確かめられたなら通す（3 値の残り 1 つ）。
    [Fact]
    public void 当日の損切りが無いと確かめられた新規建ては通る()
    {
        Evaluate(Intent(TradeSide.Buy, PositionEffect.Open), StopOutReentrySupply.NoneToday)
            .IsApproved.Should().BeTrue();
    }

    // T-10-774: 手仕舞い（Close）は損切り済みでも不明でも止めない（ADR-0009 の不変条件）。
    [Theory]
    [InlineData(StopOutStatus.StoppedOut)]
    [InlineData(StopOutStatus.Unknown)]
    public void 手仕舞いは損切り済みでも不明でも止めない(StopOutStatus status)
    {
        var supply = new StopOutReentrySupply(status, status);

        var sellClose = Evaluate(Intent(TradeSide.Sell, PositionEffect.Close), supply);
        var buyClose = Evaluate(Intent(TradeSide.Buy, PositionEffect.Close), supply);

        sellClose.IsApproved.Should().BeTrue();
        buyClose.IsApproved.Should().BeTrue();
    }

    // 既存の差金決済防止（SameDayReentry）とは別の理由である。信用口座の米国株では前者は掛からない
    // （2026-09-23 の実測はまさにこの形）ため、後者だけが立つ。
    [Fact]
    public void 差金決済防止が掛からない米国株でも損切り由来の理由は立つ()
    {
        var supply = new StopOutReentrySupply(StopOutStatus.StoppedOut, StopOutStatus.None);
        // 9/23 と同じ形: 信用口座（照会結果＝設定値の既定 Margin）・米国株・現物・当日に AAPL を売買済み。
        var snapshot = Snapshot() with
        {
            Account = new BrokerAccountState(AccountType.Margin),
            SymbolsTradedToday = new HashSet<(string, Market)> { ("AAPL", Market.UnitedStates) },
        };
        var moomooEntry = Intent(TradeSide.Buy, PositionEffect.Open) with { Mode = BrokerProvider.MoomooSimulate };
        var settings = TradingDefaults.CreateSettings() with
        {
            Stage = TradingDefaults.CreateSettings().Stage with { Mode = BrokerProvider.MoomooSimulate },
        };

        // 損切りの供給が無ければ（是正前と同じ入力）承認される＝既存統制はこの形を止めない。
        RiskEvaluator.Evaluate(moomooEntry, settings, snapshot).IsApproved.Should().BeTrue();

        var result = RiskEvaluator.Evaluate(moomooEntry, settings, snapshot, stopOuts: supply);

        result.Reasons.Should().Contain(RejectionReason.StoppedOutSameDay);
        result.Reasons.Should().NotContain(RejectionReason.SameDayReentry);
    }
}
