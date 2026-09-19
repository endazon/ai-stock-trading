using AiStockTrading.Shared.Contracts.Events;

namespace RiskManagementService.Domain;

// 🔴 FR-05, FR-10, UC-06, #852, IADR-0356: リスク管理における「見送り（<see cref="OrderDispatchForgone"/>）は
// 在庫を解放してよいか」の単一情報源。
//
// 見送りは**注文状態を持たない**（IADR-0211。ブローカーに注文が存在しない）ため、
// <see cref="OrderStatusLifecycle"/> の述語では判定できない。**足すなら述語を足す**という同ファイルの作法に従い、
// 別の列挙には別の純関数を持つ。
//
// 発注執行サービスにも見送りを作る側のコードがあるが、**サービス境界を越えて参照しない**
// （サービス間で共有する契約は <see cref="OrderDispatchForgoneReason"/> 列挙そのものであり、
// その解釈は各サービスが自分で持つ。<see cref="OrderStatusLifecycle"/> と同じ規律）。
public static class OrderDispatchForgoneLifecycle
{
    /// <summary>
    /// 🔴 その見送りが「<b>確実に未発注</b>」を意味するか。取引台帳の在庫解放
    /// （<c>GetInFlightCloseQuantity</c> が数えるのをやめる）に使う唯一の門である。
    /// <para>
    /// 🔴 <b>既定は <c>false</c>＝解放しない。</b> 確実に未発注と<b>実測できた</b>理由だけを明示的に列挙する
    /// （allowlist）。理由を見ずに一律で外すと、将来「送ったかもしれない見送り」が列挙へ足されたとき、
    /// 証券会社側で生きているかもしれない手仕舞いの押さえが解けて<b>二重決済でショート化</b>する
    /// （#848 の 2 巡目監査 B3 と同型の穴——<b>意味を変えたら、その意味に依存している既定を全部引き直す</b>）。
    /// <b>列挙漏れが安全側へ倒れる形</b>にしてあるので、新しい理由は既定のまま「解放しない」になる。
    /// </para>
    /// <para>
    /// 列挙の根拠（<c>OrderExecutionAppService.ExecuteAsync</c> を読んで実測した。いずれも
    /// <c>reservations.TryReserve</c> より<b>前</b>・ブローカーへの送信より<b>前</b>に <c>return</c> する）:
    /// <list type="bullet">
    /// <item><see cref="OrderDispatchForgoneReason.BrokerUnavailable"/>: 接続確立の失敗。
    /// IADR-0211 決定 1 が「注文がブローカーへ届き得ない段階の失敗だけ」と限定しており、
    /// 発注執行も予約を解放している（＝二重発注の窓が無いと判断している）。</item>
    /// <item><see cref="OrderDispatchForgoneReason.StopLossPriceMissing"/> /
    /// <see cref="OrderDispatchForgoneReason.StopOrderUnsupported"/> /
    /// <see cref="OrderDispatchForgoneReason.StopLossMethodNotPermitted"/>:
    /// いずれも Open の<b>発注前</b>判定（fail-closed で建玉を作らない）。</item>
    /// </list>
    /// 🔴 <b>「送信は済んだが結果が確認できない」は見送りではない</b>——それは
    /// <c>BrokerDispatchIndeterminateException</c> として伝播し、見送りイベントを作らない
    /// （IADR-0117 改定 6 / IADR-0211 の 2026-09-19 追記）。したがって本列挙に不明は入らない。
    /// </para>
    /// </summary>
    public static bool ConfirmsNoOrderPlaced(OrderDispatchForgoneReason reason) =>
        reason switch
        {
            OrderDispatchForgoneReason.BrokerUnavailable => true,
            OrderDispatchForgoneReason.StopLossPriceMissing => true,
            OrderDispatchForgoneReason.StopOrderUnsupported => true,
            OrderDispatchForgoneReason.StopLossMethodNotPermitted => true,
            // 🔴 既定は「解放しない」。新しい理由を足す人は、それが確実に未発注かを**実測して**からここへ足す。
            //
            // 🔴 判定の基準は**理由の名前ではなく「ブローカーへ送信したか」**である。
            // #873 が足す `BrokerPositionsIndeterminate` の「不明」は***建玉照会*の不明**であり、
            // IADR-0211 / IADR-0117 改定 6 が言う***発注*の不明**（送ったか分からない）ではない
            // ——前者は送信前に return するので `true` 側である。字面で `false` に落とすと、
            // 建玉が確認できない局面で見送られた手仕舞いが 30 分ロックされ #852 の実害が再発する。
            _ => false,
        };
}
