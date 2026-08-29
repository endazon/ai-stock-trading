using AiStockTrading.Shared.Contracts.Events;

namespace ReportService.Features.Reports;

// FR-10, FR-06, UC-06, ADR-0016 決定4（2026-08-06 改訂）・決定15（2026-08-06 追記）, #419, IADR-0159 決定3:
// 期間内に**強制買戻しと推定した**件を供給するポート（日報＝発生有無・月報＝発生回数）。
//
// **集計元は事後突合が推定した件数である。`RejectionReason.BuyInBanned`（拒否理由）の件数ではない**——
// 同理由は禁止期間中の発注拒否であり、**1 回の強制買戻しに対して 30 日のあいだ何度でも発生し得る**。
// 拒否件数を発生回数として報告すると実際より大きな数字が月報に載る（決定15 の明文）。
//
// **供給不達を空列へ倒さない。** 決定15 は「推定経路が入るまで発生回数は供給されない。**供給が無い間は
// 0 件と表示してはならない**（『強制買戻しは起きていない』に見えるため）」と定めている。取得できなければ
// <c>null</c> を返し、描画側が「照会できませんでした（供給元がありません）」と明記する。
public interface IBuyInInferenceRecordSource
{
    /// <summary>
    /// 期間 [fromInclusive, toInclusive]（JST 取引日）の推定。推定なしは空列、**取得不能・未供給は null**。
    /// </summary>
    Task<IReadOnlyList<BuyInInferred>?> GetInferencesAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default);
}
