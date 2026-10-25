using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-19, SC-02, ADR-0070 決定 2・5, #1407, IADR-0564 決定 1: PreCloseFlatten:Enabled / PreCloseFlatten:Minutes:<Market>
// （環境変数 PreCloseFlatten__Enabled 等）から閉場前の手仕舞いの**構成の値**を読む。未設定は既定（無効・JP/US とも 15 分）。
// IADR-0561 決定 3 では取引判断サービスが読んでいた。締め切り・テーマと同じ設定の置き場（リスク管理）へ移し、取引判断は効いている値を
// サイジング文脈で受け取る（取引判断に同じキーが残っていれば取引判断は起動を止める＝黙って効かなくしない）。巡回の間隔（IntervalSeconds）は取引判断に残す。
// 🔴 読めない値・範囲外・未知の市場は例外（構築時に読むので起動が止まる＝fail-fast。PreCloseEntryCutoffLoader と同じ向き）。
public static class PreCloseFlattenLoader
{
    public const string Section = "PreCloseFlatten";

    public static PreCloseFlattenSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = TradingDefaults.CreatePreCloseFlatten();

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

        var settings = new PreCloseFlattenSettings(enabled, minutes);
        if (settings.Violations() is { Count: > 0 } violations)
            throw new InvalidOperationException($"{Section}: {string.Join(" ", violations)} 起動を止めます。");
        return settings;
    }
}
