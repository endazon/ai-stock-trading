using System.Globalization;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-17, IADR-0076: Profitability:* から採算評価ゲートの構成を読む。
// 未設定・不正値は Default（無効・判断費用 0＝現行挙動）に倒す安全側フォールバック。Program.cs から利用し単体テストする。
public static class ProfitabilityGateOptionsLoader
{
    public static ProfitabilityGateOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Profitability");
        var options = ProfitabilityGateOptions.Default;

        // Enabled は明示 true のときのみ有効化（未設定・不正は既定 false＝現行挙動）。
        if (bool.TryParse(section["Enabled"], out var enabled))
        {
            options = options with { Enabled = enabled };
        }

        // 判断費用（円）。未設定・不正・負値は 0（ProfitabilityGateOptions 側でも正規化）。
        if (decimal.TryParse(section["DecisionCostJpy"], NumberStyles.Number, CultureInfo.InvariantCulture, out var decisionCost)
            && decisionCost > 0m)
        {
            options = options with { DecisionCostJpy = decisionCost };
        }

        return options;
    }
}
