using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-11, FR-16, ADR-0041 決定 1, #870, #859, IADR-0360 決定 2: 集計対象期間の**乖離の取り込み**
//（システム外の売買を利用者の承認つきで台帳へ取り込んだ記録）を供給するポート。
//
// 権威源はリスク管理サービスの取引台帳（`position_drift_adoptions`）であり、Database per Service（ADR-0001）を
// 跨いだ DB 直参照はせず `GET /risk-controls/drift-adoptions`（OwnerOrService）へ s2s 同期照会する。
//
// 🔴 **約定のポート（<see cref="IPeriodFillSource"/>）とは別である。** 取り込みは約定価格を持たず、
// 実現損益は**不明**である。1 本の列に混ぜると、消費側が除外を書き落としたときに「損益 0 の決済」が
// 確定値として集計される（IADR-0350 決定 4）。
//
// 🔴 **不達を空列へ倒さない**（<see cref="IPeriodFillSource"/> とは向きが違う）。空列は「該当なし」であり、
// **取り込みは本番で実際に起き得る**ため嘘になる。加えて、空列へ倒すと在庫の畳み込みからも黙って落ちて
// **実在しない建玉の評価損益**が出る。不達は `null`＝照会できていない、として報告書が明記する。
public interface IPeriodDriftAdoptionSource
{
    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（取引日）の取り込みを返す。
    /// <b><c>null</c>＝照会できていない／空列＝該当なし。</b>例外は投げない。
    /// </summary>
    Task<IReadOnlyList<PeriodDriftAdoption>?> GetDriftAdoptionsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
