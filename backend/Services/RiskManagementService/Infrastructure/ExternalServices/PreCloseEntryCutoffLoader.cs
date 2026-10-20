using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 2: PreCloseEntryCutoff:Minutes:<Market>（環境変数 PreCloseEntryCutoff__Minutes__UnitedStates 等）から
// 閉場前の新規建ての締め切り（閉場の何分前から新規建てを拒否するか）を市場ごとに読む。未設定・空の市場は既定（60 分）。
// 🔴 **読めない値・範囲外は例外**（Program.cs は構築時に読むので起動が止まる＝fail-fast）。統制の誤設定を黙って既定へ倒さない
// （MinimumEntryNotionalOptionsLoader と同じ向き）。未知の市場名のキーも例外にする（綴りの誤りで既定が効き続けるのを防ぐ）。
public static class PreCloseEntryCutoffLoader
{
    public const string Section = "PreCloseEntryCutoff:Minutes";

    public static PreCloseEntryCutoffSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var minutes = new Dictionary<Market, int>(TradingDefaults.CreatePreCloseEntryCutoff().Minutes);
        foreach (var child in configuration.GetSection(Section).GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value))
                continue;

            if (!Enum.TryParse<Market>(child.Key, ignoreCase: false, out var market) || !minutes.ContainsKey(market))
            {
                throw new InvalidOperationException(
                    $"{Section}:{child.Key} は未知の市場です（UnitedStates / Japan のいずれか）。起動を止めます。");
            }

            if (!int.TryParse(child.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException(
                    $"{Section}:{child.Key} の値「{child.Value}」を整数（分）として読めません。起動を止めます。");
            }

            minutes[market] = value;
        }

        return new PreCloseEntryCutoffSettings(minutes).Validate();
    }
}
