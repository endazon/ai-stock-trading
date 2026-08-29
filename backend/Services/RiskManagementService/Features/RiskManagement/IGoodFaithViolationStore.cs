using RiskManagementService.Domain;

namespace RiskManagementService.Features.RiskManagement;

// FR-19, FR-10, FR-11, #425, ADR-0025 決定2, ADR-0021 決定4-3, IADR-0165 決定3:
// GFV 発生回数の**自前計数の追記専用台帳**。ADR-0025 が「未決済資金による買付を自らのシステムで記録し、
// その件数を GFV 発生回数として扱う」と定めた記録先である。
//
// **永続でなければならない。** プロセス内に持つと再起動で違反記録が消え、
// 「2 件で新規建てを止める」統制が**再起動 1 回で解ける**（fail-open）。
// 口座種別の観測（IADR-0153 決定3・非永続）と設計が違うのは「集計 vs 現在値」の違いである——
// 違反件数は**再観測で復元できない履歴**であり、失われれば統制の根拠そのものが消える。
public interface IGoodFaithViolationStore
{
    /// <summary>
    /// GFV 発生 1 件を追記する。<b><c>OrderId</c> で冪等</b>（部分約定の進行・メッセージ再送で二重計上しない）。
    /// </summary>
    void Append(GoodFaithViolationRecord record);

    /// <summary>
    /// 現在の計数を返す。
    /// <para>
    /// <b>台帳そのものが権威であるため、行が 0 件でも「0 件を数えた」と主張してよい</b>——
    /// 検出は約定のたびに必ず走り、記録は永続である。したがって本メソッドは <c>null</c> を返さない。
    /// </para>
    /// <para>
    /// 「未供給」（<c>null</c>）が生じるのは<b>本ストアが判定コアへ結線されていないとき</b>だけであり、
    /// その場合は現金口座の新規建てが <c>GoodFaithViolationLimitReached</c> で止まる（fail-closed）。
    /// </para>
    /// <para>
    /// <b>解除された記録は数えない</b>（#464・ADR-0028 決定2。利用者の明示的な操作で解かれた分）。
    /// ただし<b>記録そのものは残る</b>——数えないことと消すことは別である。
    /// </para>
    /// <para>
    /// <b>失効期間は設けていない</b>（累計である）。計画（ADR-0021 / ADR-0025）は「違反記録の失効」の
    /// 期間も手段も定義しておらず、**自動失効は fail-open** であるため実装で値を発明しない
    /// （IADR-0165 決定4・計画へ環流済み）。
    /// </para>
    /// </summary>
    GoodFaithViolationTally GetTally();

    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（計上した取引日）の記録。監査・報告の明細に用いる。
    /// <para>
    /// <b>解除済みの記録も含めて返す</b>（ADR-0028 決定1「違反記録は失効させない」）——
    /// 解除は件数にしか作用せず、<b>証跡そのものは消えない</b>。
    /// </para>
    /// </summary>
    IReadOnlyList<GoodFaithViolationRecord> GetRecordedBetween(DateOnly fromInclusive, DateOnly toInclusive);

    /// <summary>
    /// FR-19, FR-11, UC-06, #464, ADR-0028 決定2, IADR-0182:
    /// <b>まだ解除されていない</b>違反記録（＝現在の計数の内訳）。解除操作の対象を決めるために用いる。
    /// </summary>
    IReadOnlyList<GoodFaithViolationRecord> GetUncleared();

    /// <summary>
    /// FR-19, FR-11, UC-06, #464, ADR-0028 決定1/決定2, IADR-0182: 解除を 1 件**追記する**。
    /// <para>
    /// 🔴 <b>違反記録の行を削除・更新してはならない。</b> ADR-0028 決定1 が「失効させない」と定めており、
    /// 解除は別の追記で表す。<see cref="GetTally"/> は解除済みを除いた件数を返すが、
    /// <see cref="GetRecordedBetween"/> は解除の有無に関わらず全件を返す。
    /// </para>
    /// <para><b><c>OrderId</c> で冪等</b>（同じ記録を二重に解除しても件数が狂わない）。</para>
    /// </summary>
    void AppendClearance(GoodFaithViolationClearance clearance);
}
