using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.State;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027, IADR-0183:
// 借株料の累計の**記録側**の型。ADR-0016 決定15 が表示と報告だけを要求し、
// **その入力を作る要件が欠けていた**空白を ADR-0027 が埋めた。
//
// **供給（実際に積み始めること）はまだ始まっていない**——ADR-0027 決定6 が
// ADR-0026 の PoC 項目 9（`ShortFeeRate` の単位確定・期限 2026-08-31）を前提としており、
// **単位を取り違えると累計は 100 倍ずれる**。本作業は記録側の型・ストア・イベントに限る
// （ADR-0027 §結果「実装は記録側を先行して設計してよい」）。

/// <summary>
/// **計上できた** 1 日分の借株料（建玉 × 取引日で 1 件）。
/// <para>
/// 🔴 <b>料率が取得できなかった日をこの型で表してはならない。</b> それは
/// <see cref="BorrowFeeUnavailableDay"/> である（ADR-0027 決定4「0 として計上しない」）。
/// 本型は <see cref="AmountUsd"/> を**非 null** で持つため、未供給を表現できない。
/// </para>
/// </summary>
/// <param name="Symbol">銘柄コード。</param>
/// <param name="Market">市場。<paramref name="Symbol"/> との組が建玉の一次識別子である（決定2）。</param>
/// <param name="TradingDay">計上の帰属日。**按分しない**（決定3）。</param>
/// <param name="RateAnnual">**計上日に照会した**年率（比率）。建玉時の料率ではない（決定4）。</param>
/// <param name="PositionValueUsd">計上の基礎となった建玉評価額（USD）。</param>
/// <param name="AmountUsd">その日の計上額（USD）。</param>
/// <param name="AccruedAt">計上を記録した時刻。</param>
public sealed record BorrowFeeAccrual(
    string Symbol,
    Market Market,
    DateOnly TradingDay,
    decimal RateAnnual,
    decimal PositionValueUsd,
    decimal AmountUsd,
    DateTimeOffset AccruedAt);

/// <summary>
/// **料率が取得できず計上できなかった** 1 日（建玉 × 取引日で 1 件）。
/// <para>
/// 🔴 <b>金額の欄を持たないのは意図である。</b> `decimal?` で持たせると
/// <c>Sum()</c> が null を 0 として畳む経路が自然に書けてしまい、
/// ADR-0027 決定4 が禁じた「未供給を 0 として積む」向きが型の上で表現可能なまま残る。
/// </para>
/// </summary>
/// <param name="Symbol">銘柄コード。</param>
/// <param name="Market">市場。</param>
/// <param name="TradingDay">計上できなかった取引日。</param>
/// <param name="Reason">取得できなかった理由（診断用）。</param>
/// <param name="ObservedAt">未供給を記録した時刻。</param>
public sealed record BorrowFeeUnavailableDay(
    string Symbol,
    Market Market,
    DateOnly TradingDay,
    string Reason,
    DateTimeOffset ObservedAt);

/// <summary>
/// 期間内の借株料の集計。**金額と未供給日数を必ず同時に運ぶ。**
/// <para>
/// 🔴 <b>金額だけを返す集計 API を作らない</b>（IADR-0183 決定3）。issue #465 の受け入れ基準は
/// 「**未供給の日がある期間の累計が、未供給の日数を伴わずに表示されない**」であり、
/// これは値の検査ではなく**型**で満たす —— 呼び出し側が未供給日数を捨てるには明示的に捨てる必要がある。
/// </para>
/// </summary>
/// <param name="AmountUsd">計上できた日の合計（USD）。**未供給の日は含まない**（0 として足していない）。</param>
/// <param name="AccruedDayCount">計上できた日数。</param>
/// <param name="UnsuppliedDayCount">
/// **料率が取得できなかった日数。** 1 日でもあれば、この合計は実費より小さい可能性がある。
/// </param>
public sealed record BorrowFeeAccrualSummary(
    decimal AmountUsd,
    int AccruedDayCount,
    int UnsuppliedDayCount)
{
    /// <summary>未供給の日が 1 日でもあるか（表示側が「未供給の日数」を併記すべきか）。</summary>
    public bool HasUnsuppliedDays => UnsuppliedDayCount > 0;
}
