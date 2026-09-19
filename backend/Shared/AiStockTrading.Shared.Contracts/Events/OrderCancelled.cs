namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, FR-19, IADR-0067: 発注済み注文が取り消された（注文履歴テレメトリ）。
// 相関キーは既存の注文系イベント（OrderApproved/OrderExecuted）と同じ DecisionId。銘柄・方向は本イベントに持たず、
// 購読側が DecisionId で OrderApproved から補完する（OrderExecuted と同じ設計・IADR-0018）。
// Reason は取消の駆動元（#141 の自動リコンサイル・#152 の pause 強制取消・時限取消）が書き込む自由文字列とする。
// 理由を列挙にすると駆動元の理由体系を本契約が先取りしてしまうため、境界を守って文字列にする（IADR-0067）。
// FR-09, UC-06, #847, IADR-0357: ObservedFilledQuantity は**取消を確認した時点でブローカーが報告した累積約定数**
// である（取消の確認は注文状態の照会で行うため、この値は必ず手元にある）。
// 🔴 **これが無いと失効通知の「未決済 N 株」が水増しされる。** 取消の確認 → 本イベントは発注執行が即座に発行するが、
// 部分約定は約定追跡（OrderFillPoller・30 秒周期）経由で台帳へ届くため、**取消が約定を追い越すのが通常**である。
// 台帳の約定累計だけで残数量を出すと、部分約定ぶんを未決済として二重に数えてしまう（実測: 承認 3,381・
// 約定 1,000 のとき remaining が 2,381 ではなく 3,381 になった）。しかも終端の記録は単調なので**訂正通知は出ない**。
// 既定 0＝「約定は観測されなかった」。受け手は台帳の累計との**大きいほう**を採る（過小に報告しない・単調）。
// 🔴 **在庫の判定には使わない。** 在庫は台帳の実約定だけで決まる（IADR-0117 決定3）——本値は通知の文面のためにある。
public record OrderCancelled(
    Guid DecisionId,
    string OrderId,
    string Reason,
    DateTimeOffset CancelledAt,
    int ObservedFilledQuantity = 0);
