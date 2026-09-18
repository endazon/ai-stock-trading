using OrderExecutionService.Domain;
using OrderExecutionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

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
        // **確認できた失敗**は Failed（SubmitFailed / Failed / Disabled / Deleted）。
        foreach (var failed in new[] { 3, 21, 22, 23 })
        {
            MMApiMoomooTradeClient.MapState(failed).Should().Be(MoomooOrderState.Failed);
        }
    }

    // 🔴 T-10-407, FR-10, UC-06, #848, IADR-0117（2026-09-19 追記・改定 3）:
    // **「状態が不明」を「確認できた失敗」に畳まない。**
    //
    // 従来は既定（`_`）が Failed であり、NONE(-1) / TimeOut(4。OpenD 定義上「結果未知」) / 未知の新コードまで
    // Failed → OrderStatus.Rejected（終端）へ倒れていた。#848 でリスク管理の取引台帳が Rejected を
    // **在庫解放の引き金**にしたため、この既定は fail-safe から fail-open へ反転した ——
    // 状態が分からないまま建玉の押さえが解け、同じ建玉を二度売れる（意図しないショート化）。
    [Fact]
    public void 結果が不明な_OpenD_状態は確認できた失敗と分けて_Unknown_にする()
    {
        // NONE / TimeOut / 本実装が知らない新コードは **不明**。
        foreach (var unknown in new[] { -1, 4, 99, 7, int.MaxValue })
        {
            MMApiMoomooTradeClient.MapState(unknown).Should().Be(MoomooOrderState.Unknown);
        }

        // 確認できた失敗は従来どおり Failed（＝ブローカー拒否として在庫解放の対象に残す）。
        foreach (var failed in new[] { 3, 21, 22, 23 })
        {
            MMApiMoomooTradeClient.MapState(failed).Should().Be(MoomooOrderState.Failed);
        }
    }

    // 🔴 T-10-407, #848: 写像を 2 段つないだ結果で固定する（片方だけ直しても意味が無い）。
    // 不明は **Rejected にならず・終端にもならない**。約定追跡が引き直し続けて本当の状態へ解決する。
    [Fact]
    public void 不明な状態は_Rejected_にも終端にもならない()
    {
        foreach (var unknown in new[] { -1, 4, 99 })
        {
            var status = MoomooBrokerAdapter.MapState(MMApiMoomooTradeClient.MapState(unknown));

            status.Should().NotBe(OrderStatus.Rejected,
                "不明を拒否へ畳むと、状態が分からないまま建玉の押さえが解ける（二重決済でショート化する）");
            status.Should().Be(OrderStatus.Accepted);
            OrderStatusLifecycle.IsTerminal(status).Should().BeFalse();
        }
    }

    // T-10-407, #848（回帰）: **確認できた失敗は従来どおり Rejected のまま**である。
    // 発注拒否で押さえが解けるのは #848 の射程内であり、ここを外すと 2 つ目の恒久ロックを作る。
    [Fact]
    public void 確認できた失敗は従来どおり拒否の終端になる()
    {
        foreach (var failed in new[] { 3, 21, 22, 23 })
        {
            var status = MoomooBrokerAdapter.MapState(MMApiMoomooTradeClient.MapState(failed));

            status.Should().Be(OrderStatus.Rejected);
            OrderStatusLifecycle.IsTerminal(status).Should().BeTrue();
        }
    }

    // T-10-407, #848（回帰）: 取消進行中（Cancelling_Part / Cancelling_All）を**非終端**へ倒している
    // 既存の配慮を壊さない。まだ板に残っている可能性があり、ここで押さえを解くと二重決済になる。
    [Fact]
    public void 取消進行中は従来どおり非終端のままにする()
    {
        foreach (var cancelling in new[] { 12, 13 })
        {
            MMApiMoomooTradeClient.MapState(cancelling).Should().Be(MoomooOrderState.Submitted);

            var status = MoomooBrokerAdapter.MapState(MMApiMoomooTradeClient.MapState(cancelling));
            status.Should().Be(OrderStatus.Accepted);
            OrderStatusLifecycle.IsTerminal(status).Should().BeFalse();
        }
    }

    [Fact]
    public void 市場を_TrdMarket_SecMarket_へ写像する()
    {
        MMApiMoomooTradeClient.MapMarket(MoomooMarket.UnitedStates).Should().Be((2, 2));   // TrdMarket_US / TrdSecMarket_US
        MMApiMoomooTradeClient.MapMarket(MoomooMarket.Japan).Should().Be((15, 51));         // TrdMarket_JP / TrdSecMarket_JP
    }
}
