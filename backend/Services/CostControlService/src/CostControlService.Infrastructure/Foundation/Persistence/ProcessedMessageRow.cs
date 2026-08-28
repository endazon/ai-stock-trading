namespace CostControlService.Infrastructure.Persistence;

// NFR（費用）, IADR-0055 決定5: 消費済みメッセージ ID の行モデル（重複排除用）。
// 月次費用台帳（cost_entries）とは別テーブルで、再配信された LlmCostIncurred の二重計上を防ぐためだけに使う。
internal sealed class ProcessedMessageRow
{
    // メッセージ ID（Wolverine の Envelope.Id。主キー＝一意制約が重複排除の要）。
    public Guid MessageId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
