using System.Globalization;
using Microsoft.Extensions.Configuration;
using TradeDecisionService.Features.TradeDecision.FlattenBeforeClose;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-19, #1396, IADR-0561 決定 3: PreCloseFlatten:IntervalSeconds（環境変数 PreCloseFlatten__IntervalSeconds）から常駐の巡回の間隔を読む。未設定は 60 秒。
// 🔴 SC-02, #1407, IADR-0564 決定 1: **PreCloseFlatten:Enabled と PreCloseFlatten:Minutes:<Market> はリスク管理の構成へ移した。** 取引判断に残っていれば
// 起動を止める —— 黙って読まないと、「構成で有効にした手仕舞い」が効かないまま気付けない（統制が緩む向き）。移し先はリスク管理の同じキー（または SC-02）。
// 読めない値・範囲外は例外（Program.cs は構築時に読むので起動が止まる＝fail-fast）。
public static class PreCloseFlattenOptionsLoader
{
    public const string Section = "PreCloseFlatten";

    public static PreCloseFlattenOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(configuration[$"{Section}:Enabled"])
            || configuration.GetSection($"{Section}:Minutes").GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value)))
        {
            throw new InvalidOperationException(
                $"{Section}:Enabled / {Section}:Minutes:<Market> は取引判断では読みません（IADR-0564 決定 1）。"
                + "リスク管理サービスの構成の同じキーか、SC-02（リスク設定画面）で設定してください。起動を止めます。");
        }

        var interval = PreCloseFlattenOptions.DefaultIntervalSeconds;
        var rawInterval = configuration[$"{Section}:IntervalSeconds"];
        if (!string.IsNullOrWhiteSpace(rawInterval)
            && !int.TryParse(rawInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval))
            throw new InvalidOperationException($"{Section}:IntervalSeconds の値「{rawInterval}」を整数（秒）として読めません。起動を止めます。");

        return new PreCloseFlattenOptions(interval).Validate();
    }
}
