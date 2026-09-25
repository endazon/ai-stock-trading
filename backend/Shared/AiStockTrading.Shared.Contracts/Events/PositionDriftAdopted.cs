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
    DateTimeOffset AdoptedAt,
    // FR-14, FR-11, UC-06, ADR-0041 決定 4, #871, IADR-0383, IADR-0423: **代理で取り込んだときの認可の主体**
    // （owner マップ機密クライアントの ID＝トークンの `azp`）。
    //
    // 取り込みの窓口は REST API と Discord Bot の両方である（ADR-0041 決定 4）。Bot は `client_credentials` の
    // トークンで呼ぶため、Actor には Bot が本文（`onBehalfOf`）で運んだ**操作した利用者**が入り、本項目に
    // **誰の資格で通ったか**が入る。利用者本人のトークンでの取り込みは Actor＝本人・本項目＝null。
    // 🔴 **それ以外の項目（Actor・Reason・数量・観測）は窓口に依らず同じ内容である**（決定 4「記録の内容は同じ」）。
    // 追加は末尾・任意（既定 null）。旧形式（AuthorizedBy 無し）の JSON はそのまま読める（IADR-0079 / IADR-0134 決定2）。
    string? AuthorizedBy = null);
