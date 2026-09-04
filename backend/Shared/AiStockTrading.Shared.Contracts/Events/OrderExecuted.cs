using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, UC-01, UC-02: 発注執行サービスが注文結果（約定・失注・取消）を確定した
//
// FR-20, FR-12, #386, IADR-0149 決定1: Provider は**実際に発注したアダプタの発注先**である。
// Stage 1 の合格判定（取引件数 100 件）は SIMULATE の約定のみで数え、内蔵 paper を算入してはならない（FR-20）。
// 判別に使える唯一正しい情報がこれであり、**既定値を与えない**——省略できるようにすると書き忘れが
// 静かに通る（IADR-0142 決定1）。なお本フィールドを持たない在庫メッセージは enum 既定 0＝InternalPaper へ
// 落ちるため、復号できない記録は**算入されない側**に倒れる（fail-safe）。
//
// NFR-02, #689, IADR-0307: 末尾 2 フィールドは取引サイクルの起点（cycle provenance）である
// （意味と null の扱いは TradeDecisionMade を参照）。**NFR-02 の終点は「記録完了」＝監査台帳への
// 記録時点**であり、その計上は本イベントを購読する AuditService が行う。起点を本イベントまで運ぶのは
// そのためであり、運ばなければ記録側で区間を閉じられない。
// 約定追跡（OrderFillPoller）が後追いで発行する遷移は OrderApproved を持たないため null になる
// （発注完了より後の状態遷移であり、NFR-01/02 の終点ではない）。
public record OrderExecuted(
    Guid DecisionId,
    string OrderId,
    OrderStatus Status,
    int FilledQuantity,
    decimal AveragePrice,
    DateTimeOffset ExecutedAt,
    BrokerProvider Provider,
    string? CycleTrigger = null,
    DateTimeOffset? CycleStartedAt = null);
