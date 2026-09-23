namespace OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;

// 🔴 FR-10, #858, IADR-0370（2026-09-24 追記 / PR #918 監査）: 乖離の取り込みに保護を追随させる直前の建玉照会が
// 不明（null）または例外だったときに投げる。**建玉が消えたと確かめられないまま保護を消さない**ため、
// 帳簿もブローカーも 1 つも変えずに処理を打ち切る。
// OrderDispatchReservationConflictException と同じ作法 —— メッセージングの再試行（2s/10s/30s）で照会をやり直し、
// 使い切ると <queue>_error へ送られる（再投入はインシデント対応。巡回が建玉を確かめられれば保護は既存の規則で収束する）。
public sealed class ProtectiveStopDriftPositionsUnknownException(
    Guid adoptionId, string symbol, Exception? innerException = null)
    : InvalidOperationException(
        $"乖離の取り込み {adoptionId}（{symbol}）: 建玉を照会できないため、保護記録とブローカー側の保護注文を変えずに"
        + "打ち切りました。建玉が消えたと確かめられないまま保護を取り消すと、実在する建玉が無保護になります。"
        + "照会が回復すれば再試行で追随します（再試行を使い切ったメッセージは _error キューに残ります）。",
        innerException)
{
    public Guid AdoptionId { get; } = adoptionId;

    public string Symbol { get; } = symbol;
}
