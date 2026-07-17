using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-19, #154, IADR-0067: 注文アクティビティ射影の状態遷移ロジック（インメモリ・EF で共有する純ヘルパ）。
// 承認で行を作り、約定・訂正・取消で更新する。銘柄・方向は承認だけが持つため、それを起点に DecisionId で相関する。
public static class OrderActivityProjection
{
    // 終端状態（生存終了）。約定なし取消の生存時間・板演出の同時生存本数の判定に用いる（IADR-0040）。
    // PartiallyFilled・Accepted は非終端で、まだ生存中として扱う。
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Expired;
}
