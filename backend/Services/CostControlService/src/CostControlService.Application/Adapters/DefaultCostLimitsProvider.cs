using AiStockTrading.Configuration.Domain;
using AiStockTrading.CostControl.Application.Ports;

namespace AiStockTrading.CostControl.Application.Adapters;

// NFR（費用）, IADR-0027: 月次費用上限の暫定供給（前提条件の既定値）。#19 のバージョン付き前提条件からの取得は #22 後続。
public sealed class DefaultCostLimitsProvider : ICostLimitsProvider
{
    public MonthlyCostLimits GetLimits() => TradingAssumptionsDefaults.Create().CostLimits;
}
