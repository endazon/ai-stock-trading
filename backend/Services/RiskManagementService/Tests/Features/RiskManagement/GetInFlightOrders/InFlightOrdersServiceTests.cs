using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using RiskManagementService.Common.Abstractions;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Features.RiskManagement.GetInFlightOrders;
using Xunit;

namespace RiskManagementService.Tests;

// NFR, #1502, IADR-0600: 未終端の注文の口は、窓（2 日）の下限を時計から決め、決済を Close・それ以外を Entry として、承認時刻の順に返す。
public class InFlightOrdersServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 6, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;

        public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);
    }

    private sealed class RecordingSource(IReadOnlyList<InFlightOrder> orders) : IInFlightOrderSource
    {
        public DateTimeOffset? Bound { get; private set; }

        public IReadOnlyList<InFlightOrder> GetInFlightOrders(DateTimeOffset approvedAtOrAfter)
        {
            Bound = approvedAtOrAfter;
            return orders;
        }
    }

    [Fact]
    public void 窓は2日で種別と状態を写し承認時刻の順に返す_1502()
    {
        InFlightOrdersService.InFlightLookback.Should().Be(TimeSpan.FromDays(2));
        var source = new RecordingSource(
        [
            new("HOOD", Market.UnitedStates, PositionEffect.Close, InFlightOrderState.Approved, Now.AddHours(-1)),
            new("NVDA", Market.UnitedStates, PositionEffect.Open, InFlightOrderState.Reserved, Now.AddHours(-3)),
        ]);

        var views = new InFlightOrdersService(source, new FixedClock()).Build();

        source.Bound.Should().Be(Now - TimeSpan.FromDays(2));
        views.Should().Equal(
            new InFlightOrderView("NVDA", Market.UnitedStates, InFlightOrderKind.Entry, InFlightOrderState.Reserved, Now.AddHours(-3)),
            new InFlightOrderView("HOOD", Market.UnitedStates, InFlightOrderKind.Close, InFlightOrderState.Approved, Now.AddHours(-1)));
    }
}
