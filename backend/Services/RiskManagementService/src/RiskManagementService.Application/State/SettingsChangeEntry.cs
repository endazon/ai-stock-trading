namespace RiskManagementService.Application.State;

// FR-11, ADR-0003, ADR-0007, ADR-0008: ガード・上限・段階設定および kill switch の変更履歴の 1 レコード。
// 「変更は利用者のみ・変更履歴を記録する」（ガード設定は ADR-0007。上限・段階・kill switch にも同じ規律を課す）を満たすため、アクター・種別・理由・前後値・日時を残す。
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

    // FR-10, ADR-0009: 取引の一時停止（pause）の発動・解除。監査（アクター・理由・日時）を kill switch と同経路で残す。
    TradingPaused,
    TradingResumed,

    // FR-20, FR-13, INDEX 決定 46, #334: 発注先（Broker Provider）の変更。
    // 計画は「発注先の変更は**日時・変更前後・理由**を変更履歴と監査ログに残す」と定める（FR-20 (2)）。
    // **末尾へ追加する**（序数 7）。本 enum は HTTP 応答で整数として往来し、画面が数値→ラベルへ写像するため、
    // 既存メンバの間へ挿入すると過去の履歴行の種別表示が黙って変わる（IADR-0134 決定2 と同じ規律）。
    BrokerProviderChanged,

    // FR-20, FR-13, SC-02, #423, IADR-0164 決定4: Stage 1 の最小取引件数（§4.1 条件 3）の変更。
    // 裁定は「下げた事実が記録に残ることを担保する」と定めており、**履歴に残ること自体が要求である**。
    // **末尾へ追加する**（序数 8）。BrokerProviderChanged と同じ規律（IADR-0134 決定2）。
    Stage1MinimumTradeCountChanged,
}
