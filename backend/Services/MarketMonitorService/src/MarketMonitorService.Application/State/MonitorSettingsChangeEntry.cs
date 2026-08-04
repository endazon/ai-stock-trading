namespace AiStockTrading.MarketMonitor.Application.State;

// FR-03, FR-11, FR-13: 監視設定（監視銘柄）変更履歴の 1 レコード。
// 「変更は利用者のみ・変更履歴を記録する」（FR-13）を満たすため、アクター・種別・理由・前後値・日時を残す。
// Risk の SettingsChangeEntry をミラーする（サービスは履歴ポートを自己所有する）。
public record MonitorSettingsChangeEntry(
    string Actor,
    MonitorSettingsChangeType ChangeType,
    string Reason,
    DateTimeOffset ChangedAt,
    string? Before = null,
    string? After = null);

// 変更対象の種別。監査・照会（UC-06）で絞り込みに用いる。
public enum MonitorSettingsChangeType
{
    // FR-03, FR-13: 監視銘柄の追加・削除。
    WatchlistSymbolAdded,
    WatchlistSymbolRemoved,
}
