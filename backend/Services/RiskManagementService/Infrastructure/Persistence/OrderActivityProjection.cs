using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-19, #154, IADR-0067: 注文アクティビティ射影の状態遷移ロジック（インメモリ・EF で共有する純ヘルパ）。
// 承認で行を作り、約定・訂正・取消で更新する。銘柄・方向は承認だけが持つため、それを起点に DecisionId で相関する。
public static class OrderActivityProjection
{
    // 終端状態（生存終了）。約定なし取消の生存時間・板演出の同時生存本数の判定に用いる（IADR-0040）。
    // PartiallyFilled・Accepted は非終端で、まだ生存中として扱う。
    //
    // #848: 定義そのものは Domain.OrderStatusLifecycle が持つ。ここは従来からの呼び出し面を保つための委譲であり、
    // 振る舞いは変わっていない。
    // 🔴 取引台帳の**在庫解放**は別の述語（AbandonsUnfilledRemainder＝Filled を含まない）を使う。問いが違う
    //（射影は「板から消えたか」、台帳は「未約定残が生き残っているか」）。どちらかへ寄せない。
    public static bool IsTerminal(OrderStatus status) => Domain.OrderStatusLifecycle.IsTerminal(status);
}
