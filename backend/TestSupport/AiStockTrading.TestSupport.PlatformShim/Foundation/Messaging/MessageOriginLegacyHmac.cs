using System.Security.Cryptography;
using System.Text;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>
/// NFR, IADR-0633 決定 4（IADR-0627 決定 2 の旧い印）: 発行元ごとの共有の鍵の HMAC-SHA256。<b>移行の間（段 1）だけ</b>使う。
/// <para>
/// 受け手も発行元の鍵を持つ形なので、受け手の Pod から発行元を名乗れる。段 2（Enforce）では構成した時点で起動を止め
/// （<see cref="MessageOriginWiringExtensions"/>）、受け手もこの印を受けない（<see cref="MessageOriginCheck.Legacy"/>）。
/// </para>
/// <para>鍵の値は例外・ログに出さない。</para>
/// </summary>
public sealed class MessageOriginLegacyHmac
{
    /// <summary>旧い印（<c>v1.</c> ＋ base64）。</summary>
    public const string SignatureHeader = "ast-origin-sig";

    /// <summary>正準形の版。</summary>
    public const string Version = "v1";

    /// <summary>鍵の最小の長さ（バイト）。</summary>
    public const int MinKeyBytes = 32;

    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly IReadOnlyDictionary<string, byte[]> _previousKeys;

    private MessageOriginLegacyHmac(IReadOnlyDictionary<string, byte[]> keys, IReadOnlyDictionary<string, byte[]> previousKeys)
    {
        _keys = keys;
        _previousKeys = previousKeys;
    }

    /// <summary>鍵の無い（旧い印を作らず・確かめられない）実体。</summary>
    public static MessageOriginLegacyHmac None { get; } = new(new Dictionary<string, byte[]>(), new Dictionary<string, byte[]>());

    /// <summary>
    /// 発行元ごとの鍵（base64）から作る。空の値は「鍵なし」。🔴 与えられているのに読めない・短い鍵は例外（起動で止める）。
    /// </summary>
    public static MessageOriginLegacyHmac Create(
        IReadOnlyDictionary<string, string?>? keys, IReadOnlyDictionary<string, string?>? previousKeys = null) =>
        new(DecodeAll(keys, "Keys"), DecodeAll(previousKeys, "PreviousKeys"));

    /// <summary>旧い鍵が 1 つでも構成されているか。</summary>
    public bool Any => _keys.Count > 0 || _previousKeys.Count > 0;

    /// <summary>この発行元の旧い鍵があるか。</summary>
    public bool HasKey(string issuerId) => _keys.ContainsKey(issuerId) || _previousKeys.ContainsKey(issuerId);

    /// <summary>旧い印を作る（現在の鍵だけで）。鍵が無ければ null。</summary>
    public string? Sign(Type messageType, Guid messageId, string issuerId, long utcTicks, object message) =>
        _keys.TryGetValue(issuerId, out var key)
            ? $"{Version}.{Convert.ToBase64String(Mac(key, messageType, messageId, issuerId, utcTicks, message))}"
            : null;

    /// <summary>旧い印が合うか（現在の鍵、次に前の鍵）。比較は固定時間。</summary>
    public bool Matches(Type messageType, Guid messageId, string issuerId, long utcTicks, object message, string signature)
    {
        var prefix = Version + ".";
        if (!signature.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(signature[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (_keys.TryGetValue(issuerId, out var key)
            && CryptographicOperations.FixedTimeEquals(presented, Mac(key, messageType, messageId, issuerId, utcTicks, message)))
            return true;

        return _previousKeys.TryGetValue(issuerId, out var previous)
            && CryptographicOperations.FixedTimeEquals(presented, Mac(previous, messageType, messageId, issuerId, utcTicks, message));
    }

    private static byte[] Mac(byte[] key, Type messageType, Guid messageId, string issuerId, long utcTicks, object message) =>
        HMACSHA256.HashData(
            key, Encoding.UTF8.GetBytes(MessageOriginSignature.Canonical(Version, messageType, messageId, issuerId, utcTicks, message)));

    private static Dictionary<string, byte[]> DecodeAll(IReadOnlyDictionary<string, string?>? source, string name)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (source is null)
            return result;

        foreach (var (issuer, value) in source)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            byte[] key;
            try
            {
                key = Convert.FromBase64String(value.Trim());
            }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    $"MessageOrigin:{name}:{issuer} が base64 として読めません（値は表示しません）。");
            }

            if (key.Length < MinKeyBytes)
                throw new InvalidOperationException(
                    $"MessageOrigin:{name}:{issuer} が短すぎます（{MinKeyBytes} バイト以上の乱数を base64 で与えてください。値は表示しません）。");

            result[issuer] = key;
        }

        return result;
    }
}
