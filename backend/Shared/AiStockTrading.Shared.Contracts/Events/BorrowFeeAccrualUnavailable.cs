using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, SC-03, UC-06, #465, ADR-0027 決定4, IADR-0183:
// **空売り建玉 1 件の 1 取引日分について、料率が取得できず借株料を計上できなかった。**
//
// ADR-0027 決定4:
// > **計上日の料率が取得できなかった日は、その日の計上を「未供給」として記録し、0 として計上しない。**
// > **0 を積むと「その日は費用が発生しなかった」と読めてしまう。**
//
// 🔴 **本イベントは金額の欄を持たない。** <see cref="BorrowFeeAccrued"/> と 1 つのイベントに畳むと、
// 金額が null 許容になり「未供給を 0 として合計へ混ぜる」経路が型の上で表現可能になる。
// **「費用が発生しなかった」と「取得できなかった」は別の事実であり、後から区別できなくなってはならない。**
public record BorrowFeeAccrualUnavailable(
    string Symbol,
    Market Market,
    // 計上できなかった取引日。
    DateOnly TradingDay,
    // 取得できなかった理由（診断用。料率照会の失敗・単位未確定など）。
    string Reason,
    DateTimeOffset ObservedAt);
