using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-10, FR-11, FR-09, UC-06, ADR-0003, #849, IADR-0350: 利用者（owner）が、台帳とブローカーの乖離を
// **観測値へ合わせる形で**取引台帳へ取り込んだ。
//
// 乖離の検知（PositionReconciliationDrift・IADR-0118）は是正を伴わない。本イベントは、その先で**利用者が承認した
// ときだけ**起きる台帳の書き換えの一次証跡である（誰が・なぜ・取り込み前後の数量・何を観測していたか）。
// 数量はいずれも**符号付き**（+ ロング / − ショート。PositionDriftItem と同じ表現）。
//
// 🔴 **実現損益は記録していない**（IADR-0350 決定 3）。システム外の売買は約定価格が分からないため、台帳へは
// 数量だけを取り込む。RealizedPnlRecorded は常に false であり、受け手が「損益 0 の決済」と読み違えないために
// 明示的に運ぶ。
//
// 🔴 EstimatedPnlInBase は**推定であり、台帳のどの数値にも入っていない**。取り込み時点の現在値（ReferencePrice）で
// 建玉を評価した参考値で、実際の決済価格ではない。現在値が取れなければどちらも null。
//
// CostBasisPrice は取り込み前の台帳の平均取得単価（ローカル通貨）。**約定価格ではない。**
//
// 発注執行側の保護記録（protective_stop_orders）の追随は本イベントを購読して行う想定である（サービス間は直接参照しない）。
public record PositionDriftAdopted(
    Guid AdoptionId,
    string Symbol,
    Market Market,
    int LedgerQuantityBefore,
    int LedgerQuantityAfter,
    int BrokerQuantity,
    DateTimeOffset ObservedAt,
    decimal CostBasisPrice,
    bool RealizedPnlRecorded,
    decimal? ReferencePrice,
    decimal? EstimatedPnlInBase,
    string Actor,
    string Reason,
    DateTimeOffset AdoptedAt);
