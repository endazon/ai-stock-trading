using System.Security.Cryptography;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, IADR-0633: 試験用の鍵の組（発行元ごとの P-256。試験の実行ごとに作る乱数で、値はどこにも書かない）。
internal static class MessageOriginTestKeys
{
    public sealed record Pair(string Private, string Public);

    private static readonly Dictionary<string, Pair> Pairs =
        MessageOriginCatalog.AllIssuers.ToDictionary(i => i, _ => NewPair(), StringComparer.Ordinal);

    public static Pair NewPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new Pair(Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static Pair For(string issuer) => Pairs[issuer];

    /// <summary>発行元の側（自分の秘密の鍵だけ）。</summary>
    public static MessageOriginSignature Signer(string issuer, Pair? pair = null) =>
        MessageOriginSignature.Create(null, null, issuer, (pair ?? For(issuer)).Private);

    /// <summary>受け手の側（全発行元の公開の鍵だけ）。</summary>
    public static MessageOriginSignature Receiver() =>
        MessageOriginSignature.Create(AllPublic());

    public static Dictionary<string, string?> AllPublic() =>
        Pairs.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.Public, StringComparer.Ordinal);

    public static string LegacyKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
