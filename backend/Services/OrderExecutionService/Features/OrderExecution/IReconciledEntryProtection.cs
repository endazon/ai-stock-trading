using OrderExecutionService.Domain;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 FR-10, FR-05, FR-12, ADR-0040 決定1, #853, IADR-0428 決定4: **突合（client order id）で「発注済み」と確定したエントリーに、
// 承認時の手法で保護レグを張る口。** 突合（OrderReservationReconciler）はブローカーの注文しか持たず、承認の文脈（手法・損切りライン）を
// 知らないため、エントリーを送る前に残した保護記録（AwaitingEntry / S1 の Active 行）を読める発注執行が実装する。
//
// 🔴 エントリーかどうかは**保護記録の有無**で判別する。予約は PositionEffect を持たず、プローブが返す注文の PositionEffect は
// Open に固定された近似である（IADR-0362 決定 3）——予約やプローブの値でエントリーと決めつけて張ると、手仕舞いレグに
// 逆指値を重ねる（決済後に残って反対建玉を生む）。記録が無いものには張らない。
public interface IReconciledEntryProtection
{
    /// <summary>
    /// 突合で確定した発注結果の記録 <paramref name="confirmed"/>（DecisionId＝エントリーの DecisionId かもしれないもの）に対し、
    /// 承認時の手法で保護レグを張る。例外は投げ得る（呼び出し側が確定済みの 1 件の出口を失わない位置で受ける）。
    /// </summary>
    Task<ReconciledEntryProtectionOutcome> ProtectAsync(ExecutionRecord confirmed, CancellationToken cancellationToken);
}

/// <summary>#853, IADR-0428 決定4: 突合で確定した 1 件の保護の結果の種類。<b>「張った」「据え置いた」「張れない」「要らない」を混ぜない。</b></summary>
public enum ReconciledEntryProtectionKind
{
    /// <summary>保護記録が無い。エントリーなら保護レグを張れない（S2 の免除・文脈を記録する前に止まった）。手仕舞い・保護レグの突合でも起きる。</summary>
    NoProtectionRecord = 0,

    /// <summary>保護記録は既に扱われている（Active の S0・完了済み）。何もしない。</summary>
    AlreadyHandled = 1,

    /// <summary>建玉が生じていない（エントリーが約定 0 で終端）。事前記録を完了にした。</summary>
    NotRequired = 2,

    /// <summary>ブローカー側の保護逆指値（S0 / S3）を受理された。</summary>
    BrokerStopPlaced = 3,

    /// <summary>保護逆指値を送信したが届いたか不明。取消も成行もせず据え置いた（常駐ガードが巡回する）。</summary>
    StopDispatchHeld = 4,

    /// <summary>保護逆指値が未受理で、エントリーの取消・成行手仕舞い（またはその失敗）へ進んだ。</summary>
    CoverageLost = 5,

    /// <summary>S1: ソフトウェア逆指値の記録は武装済み。配置の事実を通知する。</summary>
    SoftwareStopArmed = 6,
}

/// <summary>#853, IADR-0428 決定4: 突合で確定した 1 件の保護の結果。<paramref name="Events"/> は発行する順に並ぶ。</summary>
public sealed record ReconciledEntryProtectionOutcome(
    Guid DecisionId,
    ReconciledEntryProtectionKind Kind,
    IReadOnlyList<object> Events)
{
    public static ReconciledEntryProtectionOutcome Of(Guid decisionId, ReconciledEntryProtectionKind kind) =>
        new(decisionId, kind, []);
}
