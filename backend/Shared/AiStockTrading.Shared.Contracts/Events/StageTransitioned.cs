namespace AiStockTrading.Shared.Contracts.Events;

// FR-20, FR-11, UC-06, ADR-0008, IADR-0070/0082: 段階ゲート（運用段階）の遷移が承認により受理された。
// 監査サービスが購読して中央監査台帳（audit_events）へ集約する（FR-11: 全イベントの時系列記録）。
// 段階/種別は Risk.Domain の enum（TradingStage/StageTransitionKind）に依存しないよう primitive で表現する
// （Shared.Contracts → Risk.Domain の依存逆転を避ける・IADR-0082）。段階は数値割当（連続昇順・0..3）と一致。
// FR-20, FR-11, SC-02, #466, 06_daytrading-review §4.1 追補3（2026-08-07・質問票 第15回 Q13-b）, IADR-0180:
// **警告を無視して昇格した事実を記録に残す。**「昇格時点の最小取引件数の設定値」と「警告が出ていたか否か」を
// 2 項目で運ぶ。設定変更の履歴には「下げた事実」が残るが、**その設定で昇格した事実**は本イベント以外に残らない。
public record StageTransitioned(
    int Sequence,
    int FromStage,
    int ToStage,
    string Kind,
    string ApprovedBy,
    string Reason,
    DateTimeOffset OccurredAt,
    // 遷移時点の Stage 1 最小取引件数の設定値（既定 100・値域 1〜1000）。
    int Stage1MinimumTradeCount,
    // 遷移時点で**警告が出ていたか否か**。
    //
    // **設定値から後で導出しない。** 統計的根拠（100）が将来改訂されると、導出では過去の記録の解釈が
    // 黙って書き換わる。「当時警告が出ていたか」は当時の事実であり、当時の判定で凍結して記録する。
    bool Stage1BelowStatisticalBasis);
