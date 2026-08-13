using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027 決定1/決定4, IADR-0183:
// **空売り建玉 1 件の 1 取引日分の借株料を計上した。**
//
// ADR-0027 決定1 は「**日次の計上額は監査ログへ残す**」と定める ——
// **累計だけを保持すると、後から日別の内訳を復元できない。**
//
// 🔴 **本イベントは「計上できた日」だけを表す。** 料率が取得できなかった日は
// <see cref="BorrowFeeAccrualUnavailable"/> という**別のイベント**であり、金額 0 の本イベントではない。
// ADR-0027 決定4 が「0 として積まない」と明示しており、**0 円の計上と未供給は別の事実**である。
public record BorrowFeeAccrued(
    string Symbol,
    Market Market,
    // 計上の帰属日（基準タイムゾーンの取引日）。**按分しない**——この日が属する日・月へ帰属する（決定3）。
    DateOnly TradingDay,
    // **計上日に照会した**年率（比率）。建玉時の料率ではない（決定4）。
    decimal RateAnnual,
    // 計上の基礎となった建玉評価額（USD）。
    decimal PositionValueUsd,
    // その日の計上額（USD）。
    decimal AmountUsd,
    DateTimeOffset AccruedAt);
