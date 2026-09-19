using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AwesomeAssertions;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 T-10-410, FR-05, FR-10, UC-06, #852, IADR-0356: 見送り（OrderDispatchForgone）の理由のうち
// **確実に未発注**と判っているものだけが在庫解放の引き金になる、という allowlist を固定する。
//
// 既定（switch の `_`）は **false＝解放しない**である。列挙が増えたときに「送ったかもしれない見送り」が
// 黙って混じると、二重決済で意図しないショート化を作る（#848 の 2 巡目監査 B3 と同型の穴）。
public class OrderDispatchForgoneLifecycleTests
{
    // T-10-410（境界値・現行 4 値）: いずれも発注執行が **reservations.TryReserve より前**で
    // return する＝ブローカーへ 1 バイトも送っていない（OrderExecutionAppService.ExecuteAsync を実測）。
    [Theory]
    [InlineData(OrderDispatchForgoneReason.BrokerUnavailable)]
    [InlineData(OrderDispatchForgoneReason.StopLossPriceMissing)]
    [InlineData(OrderDispatchForgoneReason.StopOrderUnsupported)]
    [InlineData(OrderDispatchForgoneReason.StopLossMethodNotPermitted)]
    public void 確実に未発注と判っている理由は在庫を解放してよい(OrderDispatchForgoneReason reason)
    {
        OrderDispatchForgoneLifecycle.ConfirmsNoOrderPlaced(reason).Should().BeTrue();
    }

    // 🔴 T-10-410（否定形・最重要）: **将来足された列挙値は、既定で「解放しない」側へ倒れる。**
    // 未定義の値（列挙に無い整数）を投げて既定の向きを直接固定する —— 新しい理由を足した人が
    // ここを通らずに在庫解放を手に入れることはできない。
    [Theory]
    [InlineData(9999)]
    [InlineData(4)]
    [InlineData(-1)]
    public void 列挙に無い理由は在庫を解放しない(int futureReason)
    {
        OrderDispatchForgoneLifecycle
            .ConfirmsNoOrderPlaced((OrderDispatchForgoneReason)futureReason)
            .Should().BeFalse("既定は「解放しない」側でなければならない（列挙漏れが安全側へ倒れる形）");
    }

    // 🔴 T-10-410（境界値・列挙の要素数）: 列挙に値が**増えた**ら落ちる。落ちたら
    // 「その理由は確実に未発注か」を実測してから allowlist へ足すか、既定のまま残すかを決める。
    [Fact]
    public void 見送り理由の要素数を固定する()
    {
        Enum.GetValues<OrderDispatchForgoneReason>().Should().HaveCount(
            4,
            "見送り理由が増えたら、それが「確実に未発注」かを実測して分類し直すこと（既定は解放しない側）");
    }
}
