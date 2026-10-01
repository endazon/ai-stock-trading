using System.Text.Json;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// T-10-1806, FR-10, FR-11, ADR-0049 決定3, #1120, IADR-0465 決定2: 判断の記録に載せる「損切り幅に下限を掛けた結果」の契約。
public class StopWidthFloorContractTests
{
    // 🔴 出所の序数を固定する（0 は未指定で有効値にしない。メッセージの直列化は序数になり得る）。
    [Fact]
    public void 下限の出所の序数は固定で0は未指定()
    {
        ((int)StopWidthFloorSource.Unspecified).Should().Be(0);
        ((int)StopWidthFloorSource.Fallback2Pct).Should().Be(1);
        ((int)StopWidthFloorSource.Atr14).Should().Be(2);
        Enum.GetValues<StopWidthFloorSource>().Should().HaveCount(3);
    }

    // 🔴 否定形。項目を持たない旧い本文は null として読める（0 や既定の出所へ黙って倒れない）。
    [Fact]
    public void 項目を持たない旧い本文は幅の結果をnullとして読む_否定形()
    {
        var old = new TradeDecisionMade(
            Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 1, 100m),
            "根拠", DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(old).Replace(",\"StopWidth\":null", string.Empty, StringComparison.Ordinal);
        json.Should().NotContain("StopWidth");

        JsonSerializer.Deserialize<TradeDecisionMade>(json)!.StopWidth.Should().BeNull();
    }

    // 往復しても 5 項目が保たれる。
    [Fact]
    public void 幅の結果は直列化の往復で保たれる()
    {
        var width = new StopWidthFloorApplication(0.5m, 2m, StopWidthFloorSource.Fallback2Pct, 2m, Widened: true);
        var e = new TradeDecisionMade(
            Guid.NewGuid(),
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 1, 100m),
            "根拠", DateTimeOffset.UtcNow, StopWidth: width);

        JsonSerializer.Deserialize<TradeDecisionMade>(JsonSerializer.Serialize(e))!.StopWidth.Should().Be(width);
    }

    // T-10-1926, FR-10, FR-11, #1136, IADR-0472 決定5・決定7: 遡及の事実は往復で 10 項目を保ち、出所は序数ではなく値で一致する。
    // 下限の比率の単一情報源は共有契約の 0.02（取引判断の TradingDefaults と発注執行の遡及が同じ値を指す）。
    [Fact]
    public void T_10_1926_遡及の事実は直列化の往復で保たれ下限の比率は共有の2パーセント()
    {
        var e = new SoftwareStopLineWidened(
            Guid.NewGuid(), "NVDA", Market.UnitedStates, TradeSide.Buy, 230.82m, 226.52m, 226.2036m, 4.6164m,
            StopWidthFloorSource.Fallback2Pct, DateTimeOffset.UtcNow);

        JsonSerializer.Deserialize<SoftwareStopLineWidened>(JsonSerializer.Serialize(e)).Should().Be(e);
        StopWidthFloorDefaults.FallbackRatio.Should().Be(0.02m);
        (e.EntryPrice * StopWidthFloorDefaults.FallbackRatio).Should().Be(e.FloorPerShare);
        (e.EntryPrice - e.FloorPerShare).Should().Be(e.StopLossPrice);
    }
}
