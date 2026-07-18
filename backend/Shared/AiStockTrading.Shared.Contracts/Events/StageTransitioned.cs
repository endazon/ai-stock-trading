namespace AiStockTrading.Shared.Contracts.Events;

// FR-20, FR-11, UC-06, ADR-0008, IADR-0070/0082: 段階ゲート（運用段階）の遷移が承認により受理された。
// 監査サービスが購読して中央監査台帳（audit_events）へ集約する（FR-11: 全イベントの時系列記録）。
// 段階/種別は Risk.Domain の enum（TradingStage/StageTransitionKind）に依存しないよう primitive で表現する
// （Shared.Contracts → Risk.Domain の依存逆転を避ける・IADR-0082）。段階は数値割当（連続昇順・0..3）と一致。
public record StageTransitioned(
    int Sequence,
    int FromStage,
    int ToStage,
    string Kind,
    string ApprovedBy,
    string Reason,
    DateTimeOffset OccurredAt);
