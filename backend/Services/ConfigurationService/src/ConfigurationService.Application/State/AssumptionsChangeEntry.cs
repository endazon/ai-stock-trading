namespace AiStockTrading.Configuration.Application.State;

// FR-17, ADR-0007: 全体前提条件の変更履歴の 1 レコード（追記専用）。「変更は利用者のみ・変更履歴を記録」を満たすため、
// アクター・理由・日時・確定後バージョン・前後値を残す。
public record AssumptionsChangeEntry(
    string Actor,
    string Reason,
    DateTimeOffset ChangedAt,
    int Version,
    string? Before = null,
    string? After = null);
