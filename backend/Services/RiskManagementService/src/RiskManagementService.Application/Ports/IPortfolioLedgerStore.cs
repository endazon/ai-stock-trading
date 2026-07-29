using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Ports;

// FR-10, FR-05, FR-11, IADR-0018: 取引台帳。承認済み注文の Intent（銘柄・方向・建玉効果）を DecisionId で保持し、
// OrderExecuted の約定を OrderId で記録して DecisionId で相関する。GetFills は相関済みの LedgerFill 列（射影入力）を返す。
//
// #270, IADR-0113: 承認は追記専用（同一 DecisionId の再送は無視）だが、約定は 1 注文 = 1 行に**累積**約定数を保つ
// 単調 upsert である（追記専用ではない）。ブローカが返す約定数は累積値であり差分ではないため、行の更新が忠実な写像になる。
// いずれの操作も冪等（再送・順序前後で二重計上せず、数量は巻き戻らない）。
public interface IPortfolioLedgerStore
{
    /// <summary>承認済み注文の Intent を DecisionId で記録する。既存なら無視する（冪等）。</summary>
    void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt);

    /// <summary>
    /// 約定を OrderId で記録する。DecisionId で承認 Intent を相関して補完する。
    /// 相関する承認 Intent が無い場合は記録せず false を返す。
    /// #270, IADR-0113: <paramref name="filledQuantity"/> はブローカの**累積**約定数量（差分ではない）。
    /// 既存 OrderId は単調 upsert＝累積が増えたときだけ更新し、同数・少ない数量の後追いは無視する（冪等）。
    /// </summary>
    bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt);

    /// <summary>相関済みの約定列（射影入力）。</summary>
    IReadOnlyList<LedgerFill> GetFills();
}
