using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Domain;

// 🔴 FR-10, FR-19, #1397, IADR-0562 決定 1: テーマ（相関の高い銘柄の集まり）ごとの同時保有の上限（利用者裁定 2026-10-20・planning#809）。
// 定義は**設定値**に持つ（方針の文ではない。審査で決定的に強制する）。既定は 2 つ（暗号資産＝COIN・MARA・MSTR・HOOD、AI・半導体＝NVDA・SMCI。上限はどちらも 1）。
// どの集まりにも属さない銘柄には上限を掛けない。
public sealed record ThemeConcentrationSettings(IReadOnlyList<ThemeLimit> Themes)
{
    /// <summary>範囲外の定義があれば例外（起動を止める）。名前が空・銘柄が無い・上限が 1 未満・名前の重複は誤設定である。</summary>
    public ThemeConcentrationSettings Validate()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in Themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Name))
                throw new InvalidOperationException("ThemeConcentration:Themes の Name が空です。");
            if (!names.Add(theme.Name))
                throw new InvalidOperationException($"ThemeConcentration:Themes の Name「{theme.Name}」が重複しています。");
            if (theme.Symbols.Count == 0)
                throw new InvalidOperationException($"ThemeConcentration:Themes「{theme.Name}」に銘柄（Symbols）がありません。");
            if (theme.MaxPositions < 1)
            {
                throw new InvalidOperationException(
                    $"ThemeConcentration:Themes「{theme.Name}」の MaxPositions は 1 以上で指定してください（指定値 {theme.MaxPositions}）。");
            }
        }

        return this;
    }

    /// <summary>
    /// 新規建て（<paramref name="symbol"/>・<paramref name="market"/>）を加えると上限を超える集まり（0 件以上）。<paramref name="held"/> は保有中の銘柄と
    /// 未約定の新規建て（予約を含む）の (銘柄, 市場)。**新規建ての銘柄自身は数えない**（同じ銘柄の買い増し・売り増しは集まりの銘柄の数を増やさない）。
    /// 方向は問わない（ロングもショートも 1 銘柄として数える）。
    /// </summary>
    public IReadOnlyList<ThemeLimit> Exceeded(
        string symbol, Market market, IReadOnlySet<(string Symbol, Market Market)> held)
    {
        ArgumentNullException.ThrowIfNull(held);
        var exceeded = new List<ThemeLimit>();
        foreach (var theme in Themes)
        {
            if (!theme.Contains(symbol, market))
                continue;

            var others = held.Count(h => theme.Contains(h.Symbol, h.Market) && !SameSymbol(h.Symbol, h.Market, symbol, market));
            if (others >= theme.MaxPositions)
                exceeded.Add(theme);
        }

        return exceeded;
    }

    private static bool SameSymbol(string a, Market marketA, string b, Market marketB) =>
        marketA == marketB && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>FR-10, #1397, IADR-0562 決定 1: 1 つの集まり（名前・市場・銘柄・同時保有の上限）。銘柄の照合は前後の空白と大文字小文字を区別しない。</summary>
public sealed record ThemeLimit(string Name, Market Market, IReadOnlyList<string> Symbols, int MaxPositions)
{
    public bool Contains(string symbol, Market market) =>
        market == Market && Symbols.Any(s => string.Equals(s.Trim(), symbol.Trim(), StringComparison.OrdinalIgnoreCase));
}
