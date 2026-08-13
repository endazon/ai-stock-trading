using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027, IADR-0183:
// **借株料の日次計上（記録側）。**
//
// ADR-0027 の 5 決定を実装へ落とす。
//   決定1 計上のタイミング … 建玉を保有している**日ごと**に積む（決済時一括は SC-03 が常に 0 になるため棄却済み）
//   決定2 単位             … **建玉ごと**に積み、銘柄・口座へは**合算で導出**する（別々に積まない）
//   決定3 期間の境界       … 計上日の属する日・月へ帰属させる（**按分しない**）
//   決定4 料率変動         … **計上日の料率**を用いる。取得できなかった日は **0 として積まず未供給として記録**する
//   決定5 供給元           … 自前で日次に積み上げる
//
// 🔴 **本サービスは料率の供給元を依存に持たない。** 料率は呼び出し側が引数で与える。
// ADR-0027 決定6 が ADR-0026 の PoC 項目 9（`ShortFeeRate` の単位確定・期限 2026-08-31）を前提としており、
// **単位を取り違えると累計は 100 倍ずれる**ため、日次照会の結線（＝供給の開始）は本作業の範囲外である
// （IADR-0158 決定3 が費用計算に対して行ったのと同じ遮断）。
public sealed class BorrowFeeAccrualService(IBorrowFeeAccrualStore store)
{
    /// <summary>
    /// 日割りの分母（日数）。ADR-0027 決定5 が実弾解禁前の机上確認として
    /// 「1 日分の計上額が**料率 × 建玉評価額 ÷ 365**と一致すること」と定めた式の分母である。
    /// **暦日ベース（営業日ではない）** —— 借株は保有している暦日について発生する。
    /// </summary>
    public const int DaysPerYear = 365;

    /// <summary>
    /// 1 日分の借株料を計上する（**建玉 × 取引日で 1 件**・冪等）。
    /// <para>
    /// <paramref name="rateAnnual"/> は**計上日に照会した年率（比率）**である（決定4）。
    /// **建玉時の料率で固定しない** —— 長期保有の建玉で実費から乖離する。
    /// </para>
    /// <para>
    /// **金額を丸めない。** 丸め規則は計画のどこにも無く、実装が発明すると
    /// 「表示されている数字が何を意味するか誰も答えられない」状態（ADR-0027 が塞いだもの）へ戻る。
    /// </para>
    /// </summary>
    /// <returns>
    /// 監査へ残すイベント。**既に同じ日を計上済みなら <c>null</c>**（二重計上しない）。
    /// 呼び出し側はこれをそのまま発行する（ADR-0027 決定1「日次の計上額は監査ログへ残す」）。
    /// </returns>
    public BorrowFeeAccrued? Accrue(
        string symbol,
        Market market,
        DateOnly tradingDay,
        decimal rateAnnual,
        decimal positionValueUsd,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        // 料率・評価額が負の入力は計上しない（費用が「戻る」向きの記録を台帳へ入れない）。
        ArgumentOutOfRangeException.ThrowIfNegative(rateAnnual);
        ArgumentOutOfRangeException.ThrowIfNegative(positionValueUsd);

        var amountUsd = positionValueUsd * rateAnnual / DaysPerYear;

        var accrual = new BorrowFeeAccrual(
            symbol, market, tradingDay, rateAnnual, positionValueUsd, amountUsd, now);

        // **冪等**: 既に同じ日の行があれば書き換えず、イベントも出さない
        //（出すと監査の日次内訳が同じ日について 2 行になり、合計と食い違う）。
        return store.Record(accrual)
            ? new BorrowFeeAccrued(symbol, market, tradingDay, rateAnnual, positionValueUsd, amountUsd, now)
            : null;
    }

    /// <summary>
    /// **料率が取得できなかった日**を未供給として記録する（決定4）。
    /// <para>
    /// 🔴 <b>この経路は金額を受け取らない。</b> 0 円の計上として書けないのは意図である ——
    /// 0 を積むと「その日は費用が発生しなかった」と読め、
    /// **Stage 1 の「借株料は 1 円も掛かっていない」という誤読が構造的に起こる。**
    /// </para>
    /// </summary>
    /// <returns>監査へ残すイベント。**既に同じ日を記録済みなら <c>null</c>**。</returns>
    public BorrowFeeAccrualUnavailable? MarkUnavailable(
        string symbol,
        Market market,
        DateOnly tradingDay,
        string reason,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var day = new BorrowFeeUnavailableDay(symbol, market, tradingDay, reason, now);

        return store.RecordUnavailable(day)
            ? new BorrowFeeAccrualUnavailable(symbol, market, tradingDay, reason, now)
            : null;
    }

    /// <summary>
    /// **口座全体**の期間集計（決定2 の「合算で導出する」）。
    /// <para>期間は必須である —— 決定3 が期間の境界を定めており、「全期間の合計」は計画が定義していない。</para>
    /// </summary>
    public BorrowFeeAccrualSummary SummarizeAccount(DateOnly fromInclusive, DateOnly toInclusive) =>
        Summarize(
            store.GetAccrualsBetween(fromInclusive, toInclusive),
            store.GetUnavailableDaysBetween(fromInclusive, toInclusive));

    /// <summary>
    /// **建玉 1 件**の期間集計。生涯累計は建玉の建て日を <paramref name="fromInclusive"/> に与えて得る。
    /// <para>
    /// 生涯累計と月次合計は**別の値**である（決定3）。**同じ語で呼ばない** ——
    /// SC-03 が表示するのは建玉の生涯累計、月報が載せるのは当月分の合計である。
    /// </para>
    /// </summary>
    public BorrowFeeAccrualSummary SummarizePosition(
        BorrowFeePositionKey position, DateOnly fromInclusive, DateOnly toInclusive) =>
        Summarize(
            [.. store.GetAccrualsBetween(fromInclusive, toInclusive)
                .Where(a => a.Symbol == position.Symbol && a.Market == position.Market)],
            [.. store.GetUnavailableDaysBetween(fromInclusive, toInclusive)
                .Where(d => d.Symbol == position.Symbol && d.Market == position.Market)]);

    /// <summary>
    /// **銘柄ごと**の期間集計（決定2 の「銘柄へは合算で導出する」）。
    /// **導出でしか得られない** —— 銘柄別の合計を別に積むストアは存在しない。
    /// </summary>
    public IReadOnlyDictionary<BorrowFeePositionKey, BorrowFeeAccrualSummary> SummarizeByPosition(
        DateOnly fromInclusive, DateOnly toInclusive)
    {
        var accruals = store.GetAccrualsBetween(fromInclusive, toInclusive);
        var unavailable = store.GetUnavailableDaysBetween(fromInclusive, toInclusive);

        var keys = accruals.Select(a => new BorrowFeePositionKey(a.Symbol, a.Market))
            .Concat(unavailable.Select(d => new BorrowFeePositionKey(d.Symbol, d.Market)))
            .ToHashSet();

        return keys.ToDictionary(
            key => key,
            key => Summarize(
                [.. accruals.Where(a => a.Symbol == key.Symbol && a.Market == key.Market)],
                [.. unavailable.Where(d => d.Symbol == key.Symbol && d.Market == key.Market)]));
    }

    // **未供給の日は合計へ 1 円も足さない**（決定4）。足さないことと、足さなかった日数を必ず併せて返すことは
    // 一体である —— 未供給日数を伴わない合計は「実費」と読まれ、Stage 1 の誤読がそのまま残る。
    private static BorrowFeeAccrualSummary Summarize(
        IReadOnlyList<BorrowFeeAccrual> accruals,
        IReadOnlyList<BorrowFeeUnavailableDay> unavailable) =>
        new(accruals.Sum(a => a.AmountUsd), accruals.Count, unavailable.Count);
}
