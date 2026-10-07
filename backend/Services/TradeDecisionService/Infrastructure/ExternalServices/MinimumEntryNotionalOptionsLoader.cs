using System.Globalization;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Configuration;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, #1176, IADR-0495 決定1: Sizing:MinEntryNotionalRatio（環境変数 Sizing__MinEntryNotionalRatio）から新規建ての最小の名目額の
// しきい値を読む。未設定・空は既定（TradingDefaults.MinEntryNotionalRatio＝1%）。
// 🔴 **読めない値・範囲外（0 未満・0.25 超）は例外**（Program.cs は構築時に読むので起動が止まる＝fail-fast）。
// 採算ゲートの構成（不正は既定へ倒す）と向きが違うのは、こちらは統制であり、誤設定を黙って既定へ倒すと
// 「設定したつもりの値と効いている値が食い違う」まま運用が続くためである。
public static class MinimumEntryNotionalOptionsLoader
{
    public const string Key = "Sizing:MinEntryNotionalRatio";

    public static MinimumEntryNotionalOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw))
            return MinimumEntryNotionalOptions.Default;

        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var ratio))
        {
            throw new InvalidOperationException(
                $"{Key} の値「{raw}」を数値として読めません（equity 比。例: 0.01＝1%）。起動を止めます。");
        }

        try
        {
            return new MinimumEntryNotionalOptions(ratio);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException($"{Key} の値「{raw}」は範囲外です（{ex.Message}）。起動を止めます。", ex);
        }
    }
}
