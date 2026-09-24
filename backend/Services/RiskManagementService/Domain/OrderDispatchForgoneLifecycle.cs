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
    /// ブローカーへの送信より<b>前</b>に見送りを返す。<c>BrokerUnavailable</c> だけは予約（<c>reservations.TryReserve</c>）の
    /// <b>後</b>・送信の試み（接続確立）で失敗した場合であり、それ以外は予約より<b>前</b>である）:
    /// <list type="bullet">
    /// <item><see cref="OrderDispatchForgoneReason.BrokerUnavailable"/>: 接続確立の失敗。
    /// IADR-0211 決定 1 が「注文がブローカーへ届き得ない段階の失敗だけ」と限定している。</item>
    /// <item><see cref="OrderDispatchForgoneReason.StopLossPriceMissing"/> /
    /// <see cref="OrderDispatchForgoneReason.StopOrderUnsupported"/> /
    /// <see cref="OrderDispatchForgoneReason.StopLossMethodNotPermitted"/>:
    /// いずれも Open の<b>発注前</b>判定（fail-closed で建玉を作らない）。</item>
    /// </list>
    /// 🔴 FR-05, FR-10, #876, IADR-0398: <b>在庫を戻してよいのは「その DecisionId はもう送られない」からでもある。</b>
    /// 発注執行は見送りを発行する<b>前に</b>予約表へ見送りの終端（<c>Forgone</c>）を記録し、同じ承認の再配送では発注しない
    /// （是正前は接続確立の失敗で予約を削除し、予約前の見送りは何も残さなかったため、再配送が本物の注文を出し得た。
    /// 本述語が在庫を戻した承認の決済が、台帳の押さえの外で生きる＝二重決済でショート化）。
    /// 別の配送が予約を持っている（送ったか不明）DecisionId では、発注執行は見送りそのものを発行しない。
    /// </para>
    /// <para>
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
            // #873, IADR-0355: 決済の発注前にブローカーの実建玉と突き合わせる門。**どちらも送信前**である
            // （`OrderExecutionAppService.ExecuteAsync` を実測: 建玉照会は読み取りの
            // `GetPositionsAsync` だけで、この 2 分岐は `switch (verdict.Outcome)` の中で `return` する。
            // `reservations.TryReserve` はその**後**。#873 側のコメントも「予約はまだ取っていない」と書いている）。
            // 🔴 #876, IADR-0398: 行番号で引くのをやめた（当時 L112 / L123 / L178 と書いていたが、上流の変更のたびに腐る）。
            // 🔴 `BrokerPositionsIndeterminate` の「不明」は***建玉照会*の不明**であり、
            // ***発注*の不明**（送ったか分からない）ではない —— 下の注記を参照。
            OrderDispatchForgoneReason.BrokerPositionAbsent => true,
            OrderDispatchForgoneReason.BrokerPositionsIndeterminate => true,
            // #820, IADR-0344 追記(8) 決定4: S1 の武装の前提条件（帰属不明の建玉がある／確かめられない）。
            // **送信前**である（`OrderExecutionAppService.ExecuteAsync` を実測: この分岐は `HasUnattributedPositionAsync` の
            // 直後で `return` し、`reservations.TryReserve` とブローカーへの送信は**いずれも後**。当時は行番号で書いていた）。
            // 判定の中で叩く `GetPositionsAsync` は**読み取りの建玉照会だけ**で、注文は 1 バイトも送らない。
            // 🔴 「不明」の 3 つ目の文脈である——**建玉照会の能力が無い／照会が `null`** のときも
            // この理由で見送るが、それは***建玉照会*の不明**であって***発注*の不明**ではない（下の注記）。
            // なお本理由は **Open でしか起き得ない**（分岐が `PositionEffect.Open` の内側）ため、
            // 決済の在庫解放が実際に動くことは今のところ無い。**それでも事実として正しい側へ分類する**
            // ——既定 `false` は「送ったかもしれない」という*誤った事実*を述べることになる。
            OrderDispatchForgoneReason.UnattributedPosition => true,
            // 🔴 既定は「解放しない」。新しい理由を足す人は、それが確実に未発注かを**実測して**からここへ足す。
            //
            // 🔴 判定の基準は**理由の名前ではなく「ブローカーへ送信したか」**である。
            // #873 が足す `BrokerPositionsIndeterminate` の「不明」は***建玉照会*の不明**であり、
            // IADR-0211 / IADR-0117 改定 6 が言う***発注*の不明**（送ったか分からない）ではない
            // ——前者は送信前に return するので `true` 側である。字面で `false` に落とすと、
            // 建玉が確認できない局面で見送られた手仕舞いが 30 分ロックされ #852 の実害が再発する。
            //
            // 値を足すときの手順は `OrderDispatchForgoneLifecycleTests.見送り理由の要素数を固定する` の
            // コメントが持つ（必須 2 件・被覆 1 件・触らない 1 件）。**否定形の番兵は列挙から導いてあるので
            // 触らなくてよい**（literal の序数を書き戻すと、番兵が実在の理由へ化けて事実と逆の赤を出す）。
            _ => false,
        };
}
