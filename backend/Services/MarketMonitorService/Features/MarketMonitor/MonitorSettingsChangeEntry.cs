namespace MarketMonitorService.Features.MarketMonitor;

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
// **序数は HTTP 経路で整数として往来する**（IADR-0086 決定4）。既存メンバの序数は不変とし、新設は常に
// 末尾へ追加する（IADR-0134 決定2 と同じ規律）。
public enum MonitorSettingsChangeType
{
    // FR-03, FR-13: 監視銘柄の追加・削除。
    WatchlistSymbolAdded,
    WatchlistSymbolRemoved,

    // FR-03, FR-13, SC-01 §2, #340: 変動閾値の変更（SC-01 §2 の収集パラメータ）。末尾追加。
    MovementThresholdChanged,

    // FR-03, FR-13, SC-01 §2, #340: クールダウンの変更。末尾追加。
    CooldownChanged,
}
