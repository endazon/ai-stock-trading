using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;

namespace TradeDecisionService.Features.TradeDecision.RecordStage0Decisions;

// FR-04, FR-15, ADR-0033 決定2, #1308, IADR-0528: Stage 0 の記録器の as-of 入力の基底（価格・方針・参考情報）の供給ポート。
// 実装（外部の口）は Infrastructure にある。ここは判断時点で切る規律と、取れなかったことの扱いだけを定める。

/// <summary>
/// FR-15, #1308, IADR-0528 決定1: 判断時点 t ＝ 判断日（AsOf）の<b>通常取引の終値の時刻</b>（市場の現地時刻 → UTC）。
/// 再生は判断日の終値の情報で判断し、翌営業日の始値で約定する（<c>BacktestSimulator</c>）。参照価格は判断日の終値である
/// （IADR-0451 決定 5）。すべての as-of 入力をこの時刻で切る。
/// </summary>
public static class Stage0DecisionInstant
{
    /// <summary>判断時点（UTC）。通常取引の終値の時刻を持たない市場は null。</summary>
    /// <remarks>半日取引の日も通常の終値の時刻で切る（窓 2026-09-15〜10-09 に半日取引は無い。IADR-0528 残る制約）。</remarks>
    public static DateTimeOffset? Of(Market market, DateOnly asOf)
    {
        if (MarketSessions.RegularClose(market) is not { } close)
            return null;

        var zone = MarketHours.ZoneOf(market);
        var local = asOf.ToDateTime(close, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

/// <summary>
/// FR-15, #1308, IADR-0528 決定2・3: 基底の入力の照会が<b>失敗した</b>（取れなかった）ことを表す。
/// 🔴 「当時無かった」（休場・確定日報なし）とは別物であり、null に倒さない —— その日を「判断しない」にすると、
/// 判断が記録から黙って消え、再生の経路が AI の実際の経路から変わる。記録器はこの例外で止まり、記録集合を書き出さない。
/// </summary>
public sealed class AsOfInputUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>判断日の終値と、それより前の最後の終値（日足・前復権）。<see cref="Close"/> が null なら判断日の足が無い（休場）。</summary>
public sealed record AsOfPriceReading(DatedPrice? Close, DatedPrice? PreviousClose);

/// <summary>FR-15, #1308, IADR-0528 決定2: 判断日の価格の口。照会の失敗は <see cref="AsOfInputUnavailableException"/>。</summary>
public interface IAsOfPriceSource
{
    Task<AsOfPriceReading> GetAsync(string symbol, Market market, DateOnly asOf, CancellationToken cancellationToken = default);
}

/// <summary>
/// FR-15, FR-07, #1308, IADR-0528 決定3: 時刻 <c>at</c> の時点で確定していた日報の方針の口。
/// 確定日報が無ければ null（本番も取引しない）。照会の失敗は <see cref="AsOfInputUnavailableException"/>。
/// </summary>
public interface IAsOfDailyPolicySource
{
    Task<DailyPolicy?> GetAsync(DateTimeOffset at, CancellationToken cancellationToken = default);
}

/// <summary>参考情報の源の対象（本番の KB 検索の 2 系統: 銘柄の文書と、銘柄を持たない市場の文書）。</summary>
public enum AsOfReferenceCoverage
{
    /// <summary>銘柄の文書（KB 検索の `symbol` 属性で引く系統）。</summary>
    Symbol,

    /// <summary>市場の文書（KB 検索の `coverage=market` で引く系統）。</summary>
    Market,
}

/// <summary>
/// FR-01, FR-08, FR-15, #1308, IADR-0528 決定4: 参考情報の源 1 つの過去分の口。
/// <para>
/// 返すのは <paramref name="from"/>〜<paramref name="at"/> の間に発行された文書（収集と同じ形の <see cref="RetrievedContext"/>。
/// タグは `[種別, 源]`・発行時刻つき）。<b>null は取得に失敗した</b>（0 件の空リストとは別）。
/// 窓の両端での切り落としと並べ替え・件数の上限は呼び出し側（<see cref="AsOfReferenceAssembler"/>）がもう一度行う。
/// </para>
/// </summary>
public interface IAsOfReferenceFeed
{
    /// <summary>源の名前（収集の源の名前・出所タグと同じ語彙。例 finnhub-news）。</summary>
    string SourceName { get; }

    AsOfReferenceCoverage Coverage { get; }

    /// <param name="symbol">銘柄（<see cref="AsOfReferenceCoverage.Market"/> の源は使わない）。</param>
    Task<IReadOnlyList<RetrievedContext>?> FetchAsync(
        string symbol, Market market, DateTimeOffset from, DateTimeOffset at, CancellationToken cancellationToken = default);
}
