using AiStockTrading.Shared.Contracts.Events;

namespace OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;

// #890, FR-05, IADR-0371: **確定（MarkCompleted を commit）した 1 件ごとの出口。**
//
// 🔴 なぜ巡回の末尾ではなく 1 件ごとなのか（#890 の幾何）:
// 確定した予約は次の巡回の FindStalledReserved（State=Reserved のみ）に**載らない**。
// したがって「その予約についてこの先に出るはずだったもの」は、出なければ**永久に失われる** ——
// 「次の巡回で拾い直す」は成立しない（拾い直す対象がもう無い）。
// 巡回の末尾に出口を置くと、巡回の途中で中断されただけで（＝ローリングデプロイ・Pod 再起動が
// 巡回に重なるだけで）、確定済みの所見と OrderExecuted がまとめて消える。
//
// レイヤリング: Application（Features/）はメッセージ基盤に非依存を維持する。
// 実装（記録＋Wolverine 発行）は Worker 層（Hosted/OrderReservationReconciliationService）が持つ。
public interface IReservationReconciliationSink
{
    /// <summary>
    /// 確定した 1 件を記録し発行する。<b>記録が先・発行が後</b>（IADR-0362 決定 3 / #882 監査 N1）。
    ///
    /// 🔴 <see cref="CancellationToken"/> を取らないのは意図的である。**commit 済みの 1 件の出口は
    /// 中断させない。** 中断してよいのは「まだ確定していない予約の処理」だけであり、
    /// それは呼び出し側のループ先頭が判定する。
    /// </summary>
    Task EmitAsync(ReservationTerminalizationEmission emission);

    /// <summary>
    /// 🔴 FR-10, #853, IADR-0428 決定4: 確定した 1 件に保護レグを張った結果を記録し、そのイベントを発行する。
    /// <see cref="EmitAsync"/> の<b>後</b>に呼ばれる（確定済みの OrderExecuted と所見は既に出ている）。
    /// 「張れない」（口が無い・保護記録が無い・失敗）は<b>無音にしない</b>——突合で確定したものは Critical で残す。
    /// 取消トークンを取らない理由は <see cref="EmitAsync"/> と同じ（確定後の後始末は中断させない）。
    /// </summary>
    Task EmitProtectionAsync(ReconciledEntryProtectionEmission emission);
}

// #890, IADR-0371: 出口へ渡す 1 件。
//
// <paramref name="ProbeFinding"/> が null なのは phase-4 自己修復（記録があるのに予約が Reserved のまま）である。
// ブローカへ照会していない＝突合ではなく、記録も保護レグの有無も通常フローが決めているため、
// 「保護レグを持たない」の Critical は出さない（IADR-0362 決定 3 の「phase-4 自己修復は載せない」）。
public sealed record ReservationTerminalizationEmission(
    OrderExecuted Executed,
    ReservationReconciliationFinding? ProbeFinding);

// 🔴 #853, IADR-0428 決定4: 出口へ渡す保護の結果 1 件。
//   - Outcome が非 null: 保護の口が答えた（張った・据え置いた・張れない・要らない）。
//   - Failure が非 null: 保護の口が例外で落ちた（エントリーなら無保護の建玉が残っているかもしれない）。
//   - 両方 null: この構成には保護の口が無い（張れない）。
// ProbeConfirmed は「ブローカ照会で確定した」（phase-4 自己修復ではない）。自己修復の「記録なし」は通常フローが決めた結果なので Critical にしない。
public sealed record ReconciledEntryProtectionEmission(
    OrderExecutionService.Domain.ExecutionRecord Confirmed,
    bool ProbeConfirmed,
    ReconciledEntryProtectionOutcome? Outcome,
    Exception? Failure);
