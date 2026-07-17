namespace AiStockTrading.OrderExecution.Domain;

// FR-05, FR-19, IADR-0067: 注文ライフサイクルの種別（発注後に起きた変化）。
// 発注（Place）と終端結果（Executed）は ExecutionRecord が権威のため、本種別は訂正・取消のみを扱う。
public enum OrderLifecycleKind
{
    /// <summary>注文の訂正（数量・価格の変更）。</summary>
    Modified,

    /// <summary>注文の取消。</summary>
    Cancelled,
}

// FR-05, FR-19, FR-11, IADR-0067: 発注済み注文に起きた訂正・取消の記録（追記専用）。注文履歴テレメトリ（#154）の
// 発注執行サービス側の永続化単位。Risk 側の相場操縦検知はイベント射影から読むため（IADR-0067）、本記録は
// 発注執行サービス自身の履歴・監査・リコンサイル（#141）のための一次記録である。
public sealed record OrderLifecycleEvent(
    Guid Id,
    Guid DecisionId,
    string OrderId,
    OrderLifecycleKind Kind,
    int? PreviousQuantity,
    decimal? PreviousPrice,
    int? Quantity,
    decimal? Price,
    string Reason,
    DateTimeOffset OccurredAt);
