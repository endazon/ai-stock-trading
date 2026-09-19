using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-10, FR-19, #848, IADR-0117: リスク管理における「注文の終端」の単一情報源。
// 終端＝これ以上ブローカー側で状態が変わらない状態であり、**残りの数量が二度と約定しない**ことを意味する。
//
// 🔴 用途は 2 つあり、**問いが違うので述語も 2 つ持つ**（2026-09-19 追記・改定 2。初版は 1 つで済ませて事故った）:
//   - 注文アクティビティ射影（IADR-0067）＝「板から消えたか」        → IsTerminal（**Filled を含む**）
//   - 取引台帳（IADR-0018）＝「未約定残が生き残っているか」（#848） → AbandonsUnfilledRemainder（**Filled を含まない**）
//
// 🔴 **定義はこのファイルの外へ置かない。** 片方だけに状態が足されると、統制の一方だけが静かに緩む。
// 逆に、**片方の都合でもう片方の述語を書き換えてもいけない**（在庫解放の都合で射影の生存区間を動かすと
// 相場操縦検知の入力が変わる）。足すなら述語を足す。
// **見送り（OrderDispatchForgone）は OrderStatus を持たないため本ファイルの対象外**であり、別の純関数
// OrderDispatchForgoneLifecycle が持つ（#852 / IADR-0356。まさに「足すなら述語を足す」の実例）。
// 発注執行サービス側にも同名の純関数があるが、**サービス境界を越えて参照しない**
// （サービス間で共有する契約は `OrderStatus` 列挙そのものであり、その解釈は各サービスが自分で持つ）。
public static class OrderStatusLifecycle
{
    // 生存区間の終わり（注文アクティビティ射影・IADR-0067）。**全量約定を含む**——板から消えたかどうかの問い。
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;

    // 🔴 FR-10, UC-06, #848, IADR-0117（2026-09-19 追記・改定 2）: **未約定残が二度と約定しない**状態。
    // 取引台帳の在庫解放（GetInFlightCloseQuantity が数えるのをやめる）に使うのは**こちら**である。
    //
    // IsTerminal と違い **Filled を含めない。** 理由は 2 つあり、どちらも「含めても得が無く、害がある」:
    //   - 得が無い: 全量約定した承認は max(0, 承認数量 − 約定累計) = 0 で**自然に 0 になる**。
    //   - 害がある: 終端の記録と約定の記録は**別トランザクション**であり、前者だけが commit された区間で
    //     建玉が**丸ごと**空いて見える（実測: 建玉 100 / 処理中 0 / 利用可能 100）。その瞬間の手仕舞い要求が
    //     同じ株数をもう一度売れる。矛盾したイベント（Status=Filled かつ約定 0）では**恒久的に**在庫が戻る。
    //
    // 🔴 **IsTerminal を書き換えて片方へ寄せない。問いが違う**——射影は「板から消えたか」、台帳は
    // 「未約定残が生き残っているか」を訊いている。共有するのは列挙そのものであり、解釈は用途ごとに持つ。
    public static bool AbandonsUnfilledRemainder(OrderStatus status) =>
        status is OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;
}
