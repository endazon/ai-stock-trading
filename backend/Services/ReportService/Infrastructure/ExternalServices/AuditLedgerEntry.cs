namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-11, IADR-0199, NFR, IADR-0445 決定 4, #1059 (#753): 監査台帳の記録のうち報告書が読む 3 項目の受け皿。
// REST（`Http*Source` が JSON を読む）と gRPC（`AuditGrpcTransport` が線上の記録を写す）の**両方がこの 1 つへ写してから**、
// 各供給元の共有の解釈（`Http*Source.Build`）へ渡す。以前は 6 つの REST アダプタが同じ形の private record を 1 つずつ持っていた。
internal sealed record AuditLedgerEntry(Guid Id, string EventType, string Detail);
