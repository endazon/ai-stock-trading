using ReportService.Common.Exceptions;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06/07, IADR-0012/0024: 報告書ストア（PeriodKey ごとに 1 行＋Version 楽観排他）。確定済みは不変。
// ［2026-10-17 / #1373・IADR-0549 決定1］確定した版の写しは不変。行は、効いている確定済み日報に限り StartRevisionDraft で版 n+1 のドラフトへ戻せる（UpsertDraft は従来どおり確定済みを拒む）。
// FR-07, ADR-0068 決定2, #1373, IADR-0548: 確定のたびに確定した版の写しを別に残す（書き換えない）。最新の確定版はそこから選ぶ。
public interface IReportStore
{
    VersionedReport? Get(string periodKey);

    IReadOnlyList<TradingReport> List();

    /// <summary>
    /// FR-14, #843 項目1, IADR-0418: 会話キーと開始日だけの一覧（開始日の降順・同日は会話キーの降順）。
    /// 本文を読まない（入力補完が打鍵ごとに読むため）。
    /// </summary>
    IReadOnlyList<ReportPeriodKeyItem> ListPeriodKeys();

    /// <summary>
    /// ドラフトを作成/更新する。新規は expectedVersion=0。既存ドラフトは expectedVersion 一致時のみ更新し Version を +1 する。
    /// 確定済み報告書は不変のため更新しようとすると例外（呼び出し側で防ぐ）。不一致は <see cref="ReportConcurrencyException"/>。
    /// </summary>
    int UpsertDraft(TradingReport report, int expectedVersion);

    /// <summary>
    /// 確定する（Draft→Confirmed）。既に確定済みなら冪等（Transitioned=false・状態変化なし）。版不一致は例外。
    /// 対象が存在しなければ null を返す。
    /// <para>
    /// ADR-0068 決定2, IADR-0548 決定2: 遷移したときは、確定した版の写し（会話キー・確定後の版・確定時刻・方針・本文）を
    /// 行の確定と<b>同じ 1 回の保存</b>で足す。写しは以後書き換えない。
    /// </para>
    /// <para>
    /// 🔴 ADR-0068 決定1, IADR-0549 決定2: 同じ期間キーに確定版が既にある（版 n+1 の確定＝再確定）なら、<b>なお効いている</b>ことを同じ保存の中で確かめ、
    /// 効いていなければ <see cref="ReportNoLongerEffectiveException"/> で断る（状態は変えない）。判定は写しの通し番号の衝突で
    /// より新しい日付の日報の確定と直列化される。再確定のときは <see cref="ConfirmResult.SupersededVersion"/> にそれまでの確定版の版を返す。
    /// </para>
    /// </summary>
    ConfirmResult? Confirm(string periodKey, int expectedVersion, DateTimeOffset confirmedAt);

    /// <summary>
    /// FR-07, FR-14, ADR-0068 決定1, #1373, IADR-0549 決定1: 効いている確定済み日報に版を足すドラフト（版 n+1）を作り、新しい版を返す。1 回の保存で終える。
    /// 行が確定済みで版が <paramref name="expectedVersion"/> と一致し、日報で、効いている日報（<see cref="GetLatestConfirmed"/> の期間キー）のときだけ
    /// 行を Draft・版 +1 にして方針と本文（非空のときだけ）を差し替える。確定した版の写しは残る。
    /// 効いていなければ <see cref="ReportNoLongerEffectiveException"/>、版不一致・未確定は <see cref="ReportConcurrencyException"/>。
    /// </summary>
    int StartRevisionDraft(TradingReport draft, int expectedVersion);

    /// <summary>FR-07, ADR-0068 決定1・6, IADR-0549 決定3: この期間キーの最新の確定版（確定時刻が最新・同時刻は版番号の大きい方）。無ければ null。</summary>
    VersionedReport? GetLatestConfirmedVersion(string periodKey);

    /// <summary>FR-08, ADR-0068 決定6, IADR-0549 決定5: 期間キーごとの最新の確定版（KB の入れ直しが行の現在の状態ではなく確定版を入れるため）。</summary>
    IReadOnlyList<VersionedReport> ListLatestConfirmedVersions();

    /// <summary>
    /// 指定種別の最新の確定済み報告書。無ければ null。
    /// <para>
    /// ADR-0068 決定2, IADR-0548 決定3: 確定した版の写しから<b>二段</b>で選ぶ。1 段目は開始日が最大の期間キー
    /// （同じ開始日は会話キーの降順）、2 段目はその期間キーの中で確定時刻が最新の版（同時刻は版番号の大きい方）。
    /// 確定時刻だけで期間キーをまたいで並べない。返す版は確定後の行の版。
    /// </para>
    /// </summary>
    VersionedReport? GetLatestConfirmed(ReportKind kind);

    /// <summary>
    /// FR-15, FR-07, ADR-0033 決定2, #1308, IADR-0528 決定3: <paramref name="at"/> の時点で<b>確定していた</b>報告書のうち
    /// 最新のもの。無ければ null。
    /// <para>
    /// 🔴 ADR-0068 決定3, #1373, IADR-0548 決定4: 確定時刻が <paramref name="at"/> 以前の版<b>だけ</b>を対象に、
    /// <see cref="GetLatestConfirmed"/> と同じ二段で選ぶ。<paramref name="at"/> より後に確定した版（同じ期間キーの版 n+1 を含む）は使わない。
    /// 確定した版の写しは書き換えないため、答えは時刻 <paramref name="at"/> に <see cref="GetLatestConfirmed"/> が返した値と一致する
    /// （Stage 0 の記録が当時の方針を組むために使う。行の不変性には頼らない）。
    /// </para>
    /// </summary>
    VersionedReport? GetLatestConfirmedAsOf(ReportKind kind, DateTimeOffset at);

    /// <summary>FR-07, IADR-0042/0071: 対話的確定のレビュー局面を取得する。対象が無ければ null。</summary>
    ReportReview? GetReview(string periodKey);

    /// <summary>
    /// FR-07, #1423 M-6, ADR-0075, IADR-0583 決定2: <paramref name="kind"/> の<b>未確定</b>の報告書のうち、開始日が最大のもの（無ければ null）。
    /// 既定の実装は一覧から引く（試験の偽物のため）。本番の実装は本文を読まずに引く。
    /// </summary>
    ReportPeriodKeyItem? GetLatestUnconfirmed(ReportKind kind) =>
        List().Where(r => r.Kind == kind && r.State != ReportState.Confirmed)
            .OrderByDescending(r => r.PeriodStart).ThenByDescending(r => r.PeriodKey, StringComparer.Ordinal)
            .Select(r => new ReportPeriodKeyItem(r.PeriodKey, r.PeriodStart))
            .FirstOrDefault();

    /// <summary>
    /// FR-07, IADR-0042/0071: レビュー操作（提示・差し戻し）を <see cref="ReportReviewStateMachine"/> で検証し、遷移を永続化する。
    /// 提示・差し戻しは内容不変のため Version を上げない。拒否時は状態不変で理由を含む決定を返す。対象が無ければ null。
    /// </summary>
    ReviewDecision? ApplyReview(string periodKey, ReviewCommand command);
}
