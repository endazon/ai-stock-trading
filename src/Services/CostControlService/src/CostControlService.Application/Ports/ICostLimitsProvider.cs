using AiStockTrading.Configuration.Domain;

namespace AiStockTrading.CostControl.Application.Ports;

// NFR（費用）, FR-17, IADR-0027: 月次費用上限の供給。暫定は前提条件の既定値、実は #19 のバージョン付き前提条件（#22 後続）。
public interface ICostLimitsProvider
{
    MonthlyCostLimits GetLimits();
}
