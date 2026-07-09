namespace AiStockTrading.RiskManagement.Application.State;

// FR-11, ADR-0007: ガード・上限・段階設定および kill switch の変更履歴の 1 レコード。
// 「変更は利用者のみ・変更履歴を記録する」（ADR-0007）を満たすため、アクター・種別・理由・前後値・日時を残す。
public record SettingsChangeEntry(
    string Actor,
    SettingsChangeType ChangeType,
    string Reason,
    DateTimeOffset ChangedAt,
    string? Before = null,
    string? After = null);

// 変更対象の種別。監査・照会（UC-06/UC-07）で絞り込みに用いる。
public enum SettingsChangeType
{
    Guard,
    Limits,
    Stage,
    KillSwitchEngaged,
    KillSwitchDisengaged,
}
