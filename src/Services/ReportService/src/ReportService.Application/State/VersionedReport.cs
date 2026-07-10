using AiStockTrading.Report.Domain;

namespace AiStockTrading.Report.Application.State;

// FR-07: 報告書と楽観排他用のバージョン。
public sealed record VersionedReport(TradingReport Report, int Version);
