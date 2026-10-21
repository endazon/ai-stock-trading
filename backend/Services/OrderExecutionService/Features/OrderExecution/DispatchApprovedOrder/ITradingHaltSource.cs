namespace OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;

// 🔴 FR-10, ADR-0009 決定 3・4, NFR-04, IADR-0567 決定 1・2: 新規建てを送る直前に読む、利用者の 2 つの停止（kill switch・一時停止）の
// 現在の状態。持ち主はリスク管理であり、発注執行は読むだけである（規則〔新規建てだけを止める〕は発注執行の判定が持つ）。
public interface ITradingHaltSource
{
    /// <summary>
    /// 停止の状態。<b>確かめられなければ <c>null</c>（不明）</b>——照会の失敗・タイムアウト・解釈できない応答・照会先の未構成・
    /// 呼び出し元の取り消しのいずれでも例外を投げずに <c>null</c> を返す（呼び出し元は不明を「送らない」へ倒す）。
    /// </summary>
    Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>停止の状態（どちらも既知）。</summary>
public sealed record TradingHaltState(bool KillSwitchEngaged, bool TradingPaused);
