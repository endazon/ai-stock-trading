using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, ADR-0040 決定1, #826 項目 3, IADR-0342（2026-09-26 追記）（T-10-1575）: 🔴 **既知の制約を固定する試験**。
// 同じ銘柄・方向に S0（ブローカー側逆指値）の建玉と S2（逆指値なしの建玉）が併存すると、S2 は保護記録を持たないため
// S0 の行から見た建玉残から差し引けない（S1 の行は差し引く）。S0 の建玉が消えても S2 の数量が残っていれば、ガードは
// 「建玉あり」と読んで S0 の逆指値を取り消さない。直し方は未決（利用者への問い）。直したら本試験の期待を改めること。
// 今は起きない: SIMULATE ではブローカーが逆指値を受け付けず S0 の建玉が残らず、S2 は SIMULATE でしか選べない。
public class ProtectiveStopNettingS2CoexistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    private static ProtectiveStopOrder Row(
        StopLossExecutionMethod mechanism, int quantity, int? remainingProtected = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), mechanism == StopLossExecutionMethod.BrokerStopOrder ? "stop-s0" : string.Empty,
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, quantity, 950m, 1m, 1,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5),
            Mechanism: mechanism, RemainingProtected: remainingProtected);

    private static BrokerPositionSnapshot Long(int qty) => new("AAPL", Market.UnitedStates, qty, 1_000m);

    // T-10-1575: S0 の建玉 5 株が消え、S2 の建玉 10 株だけが残った口座。S0 の行から見た建玉残は 10（S2 を差し引けない）。
    // 対照: 残った 10 株が S1 の建玉（有効な記録が 10 株を主張）なら差し引いて 0（S0 の逆指値は取り消される側）。
    [Fact]
    public void S2の建玉はS0の建玉残から差し引けない_既知の制約()
    {
        var s0 = Row(StopLossExecutionMethod.BrokerStopOrder, quantity: 5);

        ProtectiveStopNetting.RemainingPositionFor(s0, [Long(10)], [s0]).Should().Be(
            10, "S2 は保護記録を持たないため、S0 の建玉が消えても S2 の 10 株が「建玉あり」に見える（#826 項目 3 の残り）");

        var s1 = Row(StopLossExecutionMethod.SoftwareStop, quantity: 10, remainingProtected: 10);
        ProtectiveStopNetting.RemainingPositionFor(s0, [Long(10)], [s0, s1]).Should().Be(
            0, "S1 の建玉は有効な記録の残保護数量で差し引く（S0 の逆指値を生かし続けない）");

        ProtectiveStopNetting.RemainingPositionFor(s0, [Long(15)], [s0]).Should().Be(
            15, "S0 の 5 株と S2 の 10 株が併存しても区別できず、S0 の側から見た建玉残は 15");
    }
}
