using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Backtest.Domain;

// FR-15, ADR-0008, IADR-0038: 銘柄の決定的仮名化（pseudonymization・検証条件①）。同一銘柄は常に同一の匿名 ID に写す。
// 目的は「LLM がプレーンテキストのティッカーを文脈から認識するのを防ぐ」ことであり、本用途には十分。
// ただし無鍵の決定的 SHA-256（先頭 4 バイトを 16 進化）であるため、上場銘柄コードのような小さな既知空間では
// 総当たりでの再特定は理論上可能（暗号学的な秘匿ではない）。厳格な秘匿が要る場合は鍵付き HMAC 等を検討する。
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
