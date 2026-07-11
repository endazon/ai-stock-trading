using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008, IADR-0038: 銘柄の決定的匿名化（検証条件①）。同一銘柄は常に同一の匿名 ID に写す一方、
// 匿名 ID から元銘柄を復元できないようにして LLM の銘柄同定を防ぐ。SHA-256 の先頭バイトを 16 進化した安定 ID。
public static class SymbolAnonymizer
{
    // 決定的匿名 ID（例: "SYM_1a2b3c4d"）。市場を混ぜることで市場跨ぎの衝突を避ける。
    public static string Anonymize(string symbol, Market market)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{market}:{symbol}"));
        // 先頭 4 バイト（32bit）を 16 進化。衝突確率は検証用途に十分小さい。
        var hex = Convert.ToHexStringLower(bytes.AsSpan(0, 4));
        return $"SYM_{hex}";
    }

    // バーの銘柄コードのみ匿名 ID に置換し、価格・日付・市場・出来高は保持する。
    public static PriceBar AnonymizeBar(PriceBar bar) =>
        bar with { Symbol = Anonymize(bar.Symbol, bar.Market) };
}
