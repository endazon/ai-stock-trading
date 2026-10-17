using OrderExecutionService.Domain;

namespace OrderExecutionService.Features.OrderExecution;

// 🔴 FR-10, FR-05, FR-12, ADR-0040 決定1, #853, IADR-0428 決定4: **突合（client order id）で「発注済み」と確定したエントリーに、
// 承認時の手法で保護レグを張る口。** 突合（OrderReservationReconciler）はブローカーの注文しか持たず、承認の文脈（手法・損切りライン）を
// 知らないため、エントリーを送る前に残した保護記録（AwaitingEntry / S1 の Active 行）を読める発注執行が実装する。
//
// 🔴 エントリーかどうかは**保護記録の有無**で判別する。予約は PositionEffect を持たず、プローブが返す注文の PositionEffect は
// Open に固定された近似である（IADR-0362 決定 3）——予約やプローブの値でエントリーと決めつけて張ると、手仕舞いレグに
// 逆指値を重ねる（決済後に残って反対建玉を生む）。記録が無いものには張らない。
// ［#1262, IADR-0515 追記(2)］予約は建て・決済の別を持つようになり、突合で確定した記録の PositionEffect はその値で書かれる。ただし列を足す前の
// 予約は持たない（照会の Open のまま）ため、判別は引き続き保護記録で行う（この規則は変えない）。
public interface IReconciledEntryProtection
{
    /// <summary>
    /// 突合で確定した発注結果の記録 <paramref name="confirmed"/>（DecisionId＝エントリーの DecisionId かもしれないもの）に対し、
    /// 承認時の手法で保護レグを張る。例外は投げ得る（呼び出し側が確定済みの 1 件の出口を失わない位置で受ける）。
    /// </summary>
    Task<ReconciledEntryProtectionOutcome> ProtectAsync(ExecutionRecord confirmed, CancellationToken cancellationToken);

    /// <summary>
    /// 🔴 FR-05, FR-10, ADR-0040 決定1, #1343, IADR-0550 決定1: <paramref name="decisionId"/> が<b>送る前に保護の記録を残した新規建て</b>
    /// （S1 の行・S0 / S3 の AwaitingEntry。<c>EntryDecisionId</c> がこの値の行）かを返す。突合が「未発注」と確定した予約を
    /// 削除（再発注の許可）ではなく見送り（Forgone）として閉じるかの判別に使う。既定は「記録なし」（口を持たない構成・試験の差し替え）。
    /// </summary>
    bool HasEntryProtectionRecord(Guid decisionId) => false;

    /// <summary>
    /// 🔴 FR-05, FR-10, ADR-0040 決定1, #1343, IADR-0550 決定2: 突合が「未発注」と確定して予約を<b>見送り（Forgone）へ移した後に</b>、
    /// その新規建てが送る前に残した保護の記録（S1 の未確定・未到達の行／S0 / S3 の AwaitingEntry）を完了にする。
    /// 発注の記録があるもの・到達や決済の経路に入ったもの・並行に更新されたものは閉じない（<see cref="UnplacedEntryProtectionClosure.LeftOpen"/>）。
    /// 例外は投げ得る（呼び出し側は予約の見送りを巻き戻さずに受ける）。既定は「記録なし」。
    /// </summary>
    UnplacedEntryProtectionClosure CloseUnplacedEntryProtection(Guid decisionId) => UnplacedEntryProtectionClosure.NoProtectionRecord;
}

/// <summary>
/// 🔴 #1343, IADR-0550 決定2: 未発注と確定した新規建ての保護の記録を閉じた結果。<b>「閉じた」「元から無い・閉じている」「閉じられない」を混ぜない。</b>
/// </summary>
public enum UnplacedEntryProtectionClosure
{
    /// <summary>保護の記録が無い（新規建てではない・S2 の免除・記録を残す前に止まった）。</summary>
    NoProtectionRecord = 0,

    /// <summary>S1（ソフトウェア逆指値）の未確定・未到達の行を完了にした。</summary>
    SoftwareStopClosed = 1,

    /// <summary>S0 / S3 の承認時の保護の文脈（AwaitingEntry）を完了にした。</summary>
    AwaitingEntryClosed = 2,

    /// <summary>記録は既に完了している。何もしない。</summary>
    AlreadyCompleted = 3,

    /// <summary>
    /// 閉じなかった（発注の記録がある・S1 が到達や決済の経路に入っている・建玉の確定が進んでいる・並行に更新された・S0 / S3 の Active）。
    /// 人が発注経路の Runbook「発注の記録が無いまま残った S1 の記録を閉じる」（AwaitingEntry も同じ節）に従って確かめる。
    /// </summary>
    LeftOpen = 4,
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

    /// <summary>
    /// #853（PR #1005 監査 5）: エントリーではなく、Active な保護記録の<b>保護レグ</b>（据え置いた逆指値・ガードの成行手仕舞い）の突合だった。
    /// 保護記録の巡回（常駐ガード）が結果を引き取るので、ここでは何もしない（「保護の記録が無い」とは言わない）。
    /// </summary>
    ProtectiveLeg = 7,
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
