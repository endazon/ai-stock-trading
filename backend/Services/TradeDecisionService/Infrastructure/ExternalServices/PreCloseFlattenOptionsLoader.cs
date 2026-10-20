using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;
using TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 3: PreCloseFlatten:Enabled / PreCloseFlatten:Minutes:<Market> / PreCloseFlatten:IntervalSeconds
// （環境変数 PreCloseFlatten__Enabled 等）から閉場前の手仕舞いの構成を読む。未設定は既定（無効・JP/US とも 15 分・60 秒）。
// 🔴 読めない値・範囲外・未知の市場は例外（Program.cs は構築時に読むので起動が止まる＝fail-fast。MinimumEntryNotionalOptionsLoader と同じ向き）。
public static class PreCloseFlattenOptionsLoader
{
    public const string Section = "PreCloseFlatten";

    public static PreCloseFlattenOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = PreCloseFlattenOptions.Default;

        var enabled = defaults.Enabled;
        var rawEnabled = configuration[$"{Section}:Enabled"];
        if (!string.IsNullOrWhiteSpace(rawEnabled) && !bool.TryParse(rawEnabled, out enabled))
            throw new InvalidOperationException($"{Section}:Enabled の値「{rawEnabled}」を true / false として読めません。起動を止めます。");

        var minutes = new Dictionary<Market, int>(defaults.Minutes);
        foreach (var child in configuration.GetSection($"{Section}:Minutes").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value))
                continue;

            if (!Enum.TryParse<Market>(child.Key, ignoreCase: false, out var market) || !minutes.ContainsKey(market))
                throw new InvalidOperationException($"{Section}:Minutes:{child.Key} は未知の市場です（UnitedStates / Japan のいずれか）。起動を止めます。");

            if (!int.TryParse(child.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException($"{Section}:Minutes:{child.Key} の値「{child.Value}」を整数（分）として読めません。起動を止めます。");

            minutes[market] = value;
        }

        var interval = defaults.IntervalSeconds;
        var rawInterval = configuration[$"{Section}:IntervalSeconds"];
        if (!string.IsNullOrWhiteSpace(rawInterval)
            && !int.TryParse(rawInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval))
            throw new InvalidOperationException($"{Section}:IntervalSeconds の値「{rawInterval}」を整数（秒）として読めません。起動を止めます。");

        return new PreCloseFlattenOptions(enabled, minutes, interval).Validate();
    }
}
