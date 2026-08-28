using ReportService.Domain;

namespace ReportService.Application.State;

// FR-07: 報告書と楽観排他用のバージョン。
public sealed record VersionedReport(TradingReport Report, int Version);
