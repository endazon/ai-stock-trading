namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-11, IADR-0199, NFR, IADR-0445 決定 4, #1059 (#753): 監査台帳の記録のうち報告書が読む 4 項目の受け皿。
// REST（`Http*Source` が JSON を読む）と gRPC（`AuditGrpcTransport` が線上の記録を写す）の**両方がこの 1 つへ写してから**、
// 各供給元の共有の解釈（`Http*Source.Build`）へ渡す。以前は 6 つの REST アダプタが同じ形の private record を 1 つずつ持っていた。
//
// FR-06, IADR-0516（2026-10-08 追記）, #1255: `OccurredAt` は台帳の発生時刻（照会の絞り込みと同じ列）。本文を復元できなかった記録を
// 報告書のセッションの窓に置くために読む。**`null` は「時刻なし」**（発生時刻を運ばない旧版の提供側）であり、従来どおり照会の範囲で数える。
internal sealed record AuditLedgerEntry(Guid Id, string EventType, string Detail, DateTimeOffset? OccurredAt = null);
