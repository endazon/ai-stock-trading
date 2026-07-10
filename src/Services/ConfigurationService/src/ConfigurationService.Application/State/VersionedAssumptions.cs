using AiStockTrading.Configuration.Domain;

namespace AiStockTrading.Configuration.Application.State;

// FR-17: 現在の全体前提条件とそのバージョン。報告書は生成時 Version を凍結参照できる（API 公開）。
public sealed record VersionedAssumptions(TradingAssumptions Assumptions, int Version);
