using System.Globalization;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;
using RiskManagementService.Domain;

namespace RiskManagementService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-19, #1397, IADR-0562 決定 1: ThemeConcentration:Themes:<i>:Name / :Market / :MaxPositions / :Symbols:<j>
// （環境変数 ThemeConcentration__Themes__0__Name 等）からテーマごとの同時保有の上限の定義を読む。
// **節が無ければ既定**（暗号資産＝COIN・MARA・MSTR・HOOD、AI・半導体＝NVDA・SMCI。上限はどちらも 1）。**節があれば既定を丸ごと置き換える**
// （集まりを足す・外すのは定義の全体を書くことで行う。既定へ部分的に足し引きする書き方は持たない＝どの定義が効いているかを 1 か所で読める）。
// Market は省略すると UnitedStates。🔴 読めない値・範囲外（名前が空・銘柄なし・上限 1 未満・名前の重複・未知の市場）は起動を止める。
public static class ThemeConcentrationLoader
{
    public const string Section = "ThemeConcentration:Themes";

    public static ThemeConcentrationSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var children = configuration.GetSection(Section).GetChildren().ToList();
        if (children.Count == 0)
            return TradingDefaults.CreateThemeConcentration();

        var themes = new List<ThemeLimit>();
        foreach (var child in children.OrderBy(c => int.TryParse(c.Key, out var i) ? i : int.MaxValue))
        {
            var name = child["Name"] ?? string.Empty;

            var market = Market.UnitedStates;
            var rawMarket = child["Market"];
            if (!string.IsNullOrWhiteSpace(rawMarket) && !Enum.TryParse(rawMarket, ignoreCase: false, out market))
                throw new InvalidOperationException($"{Section}:{child.Key}:Market「{rawMarket}」は未知の市場です（UnitedStates / Japan）。起動を止めます。");

            var rawMax = child["MaxPositions"];
            if (!int.TryParse(rawMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
                throw new InvalidOperationException($"{Section}:{child.Key}:MaxPositions「{rawMax}」を整数として読めません。起動を止めます。");

            var symbols = child.GetSection("Symbols").GetChildren()
                .Select(s => s.Value?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            themes.Add(new ThemeLimit(name.Trim(), market, symbols, max));
        }

        return new ThemeConcentrationSettings(themes).Validate();
    }
}
