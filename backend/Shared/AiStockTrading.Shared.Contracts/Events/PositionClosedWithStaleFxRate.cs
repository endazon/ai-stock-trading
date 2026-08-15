using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, FR-09, #381, ADR-0022 決定2・決定5, IADR-0197, IADR-0198: **鮮度切れ（30 日超）のレートで
// 決済意図を作った**。手仕舞いは止めない（計画どおり）が、**劣化した値で取引したことは記録に残す。**
//
// 🔴 **`FxRateStale` とは別の事実である。** あちらは「**レート源の状態**が閾値を超えた」、
// こちらは「**その値で実際に取引した**」。前者は 1 日 1 回へ抑止するが、
// **こちらは抑止しない** —— **取引は 1 件ずつ残さなければ、後から件数も金額も復元できない。**
//
// 🔴 **本イベントが「いつのレートで決済したか」を 7 年保持へ入れる唯一の経路である。**
// 監査台帳の行（`ApprovedOrderRow`）は `FxRateToBase`（数値）だけを持ち**観測日の列が無い**が、
// 台帳は**イベント全量を JSON で保存する**ため、<see cref="RateAsOf"/> を載せれば
// **EF マイグレーションなしで観測日が残る**（IADR-0198 決定3）。
public record PositionClosedWithStaleFxRate(
    string Symbol,
    Market Market,
    string Quote,
    int Quantity,
    decimal FxRateToBase,
    DateTimeOffset RateAsOf,
    double AgeDays,
    DateTimeOffset OccurredAt);

// 🔴 **絶対上限（MaxAgeDays）は載せない。** 判断サービスは上限値を知らない（鮮度装飾が持つ）。
// **知らない値を「それらしく」載せると、台帳に嘘が残る**——本 PR が守ろうとしているものの逆である。
// 上限は構成値であり、必要なら `FxOptions` 側から復元できる。
