using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// #13, FR-05: MMApiMoomooTradeClient の SDK 非依存な写像ロジック（OpenD OrderStatus・市場）を単体検証する
// （実 OpenD 不使用）。実結合の疎通は別途 live 検証（常駐 OpenD 前提）。
// 注: 引数の enum は internal のため [Theory] の InlineData に置けず（CS0051）、[Fact] で網羅する。
public class MMApiMoomooTradeClientMappingTests
{
    [Fact]
    public void OpenD_OrderStatus_を_MoomooOrderState_へ写像する()
    {
        MMApiMoomooTradeClient.MapState(0).Should().Be(MoomooOrderState.Submitting);   // Unsubmitted
        MMApiMoomooTradeClient.MapState(1).Should().Be(MoomooOrderState.Submitting);   // WaitingSubmit
        MMApiMoomooTradeClient.MapState(2).Should().Be(MoomooOrderState.Submitting);   // Submitting
        MMApiMoomooTradeClient.MapState(5).Should().Be(MoomooOrderState.Submitted);    // Submitted
        MMApiMoomooTradeClient.MapState(10).Should().Be(MoomooOrderState.FilledPart);  // Filled_Part
        MMApiMoomooTradeClient.MapState(11).Should().Be(MoomooOrderState.FilledAll);   // Filled_All
        MMApiMoomooTradeClient.MapState(12).Should().Be(MoomooOrderState.Submitted);   // Cancelling_Part（進行中）
        MMApiMoomooTradeClient.MapState(13).Should().Be(MoomooOrderState.Submitted);   // Cancelling_All
        MMApiMoomooTradeClient.MapState(14).Should().Be(MoomooOrderState.Cancelled);   // Cancelled_Part
        MMApiMoomooTradeClient.MapState(15).Should().Be(MoomooOrderState.Cancelled);   // Cancelled_All
        MMApiMoomooTradeClient.MapState(24).Should().Be(MoomooOrderState.Cancelled);   // FillCancelled
        // 失敗・不明系は安全側で Failed。
        foreach (var failed in new[] { 3, 4, 21, 22, 23, -1 })
        {
            MMApiMoomooTradeClient.MapState(failed).Should().Be(MoomooOrderState.Failed);
        }
    }

    [Fact]
    public void 市場を_TrdMarket_SecMarket_へ写像する()
    {
        MMApiMoomooTradeClient.MapMarket(MoomooMarket.UnitedStates).Should().Be((2, 2));   // TrdMarket_US / TrdSecMarket_US
        MMApiMoomooTradeClient.MapMarket(MoomooMarket.Japan).Should().Be((15, 51));         // TrdMarket_JP / TrdSecMarket_JP
    }
}
