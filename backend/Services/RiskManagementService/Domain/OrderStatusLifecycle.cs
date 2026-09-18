using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// FR-10, FR-19, #848, IADR-0117: リスク管理における「注文の終端」の単一情報源。
// 終端＝これ以上ブローカー側で状態が変わらない状態であり、**残りの数量が二度と約定しない**ことを意味する。
//
// 用途は 2 つあり、いずれも同じ定義でなければならない:
//   - 取引台帳（IADR-0018）: 終端になった決済承認を「処理中の決済」から除く（#848）。
//   - 注文アクティビティ射影（IADR-0067）: 注文の生存区間の終わり（相場操縦検知の入力）。
//
// 🔴 **定義を 2 か所に置かない。** 片方だけに状態が足されると、統制の一方だけが静かに緩む。
// 発注執行サービス側にも同名の純関数があるが、**サービス境界を越えて参照しない**
// （サービス間で共有する契約は `OrderStatus` 列挙そのものであり、その解釈は各サービスが自分で持つ）。
public static class OrderStatusLifecycle
{
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;
}
