using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>
/// 🔴 NFR, FR-10, IADR-0633 決定 1・2（IADR-0627 決定 2 を置き換える）: 統制の入力になるメッセージの発行元の署名
/// （ECDSA P-256 / SHA-256。発行元は自分の<b>秘密の鍵</b>で署名し、受け手は確かめる発行元の<b>公開の鍵</b>だけを持つ）。
/// <para>
/// 署名の対象は <b>型・メッセージ ID・発行元・時刻・本文の正規形</b>である（<see cref="Canonical(string, Type, Guid, string, long, object)"/>）。
/// 文字列は長さを前置して区切りの偽装を防ぐ。本文の正規形は固定の設定の JSON で、decimal の末尾のゼロ・時刻のオフセットを
/// 正規化する（Wolverine の直列化の往復で値の表し方が変わっても署名は合う。往復は試験で型ごとに固定する）。
/// </para>
/// <para>
/// 受け手の構成（公開の鍵だけ）からは署名できない（<see cref="Sign"/> は null）。公開の鍵の欄に秘密の鍵を、署名の鍵の欄に
/// 公開の鍵を与えると例外（起動で止める）。鍵の値は例外・ログに出さない。
/// </para>
/// </summary>
public sealed class MessageOriginSignature
{
    /// <summary>発行元の識別子（例 <c>trade-decision</c>）。</summary>
    public const string IssuerHeader = "ast-origin";

    /// <summary>メッセージ ID（Guid）。再送の検出に使う。</summary>
    public const string IdHeader = "ast-origin-id";

    /// <summary>発行の時刻（UTC の ticks）。</summary>
    public const string TimeHeader = "ast-origin-at";

    /// <summary>署名（<c>v2.</c> ＋ base64。旧い HMAC の印 <see cref="MessageOriginLegacyHmac.SignatureHeader"/> とは別の見出し）。</summary>
    public const string SignatureHeader = "ast-origin-sig2";

    /// <summary>正準形の版。</summary>
    public const string Version = "v2";

    /// <summary>曲線 P-256（secp256r1）の OID。</summary>
    public const string CurveOid = "1.2.840.10045.3.1.7";

    // IEEE P1363 の固定長（r ‖ s。P-256 は 32 + 32 バイト）。
    private const int SignatureBytes = 64;

    private static readonly JsonSerializerOptions BodyOptions = CreateBodyOptions();

    private readonly string? _signingIssuer;
    private readonly LockedKey? _signingKey;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<LockedKey>> _verifyKeys;

    private MessageOriginSignature(
        string? signingIssuer, LockedKey? signingKey, IReadOnlyDictionary<string, IReadOnlyList<LockedKey>> verifyKeys)
    {
        _signingIssuer = signingIssuer;
        _signingKey = signingKey;
        _verifyKeys = verifyKeys;
    }

    /// <summary>
    /// 鍵から作る。<paramref name="verifyKeys"/>・<paramref name="previousVerifyKeys"/> は発行元 → 公開の鍵（SubjectPublicKeyInfo DER の
    /// base64）、<paramref name="signingKey"/> は <paramref name="signingIssuer"/> の秘密の鍵（PKCS#8 DER の base64）。空の値は「鍵なし」。
    /// 🔴 与えられているのに読めない・P-256 でない・秘密と公開を取り違えた鍵は例外（起動で止める。黙って署名なしへ倒さない）。
    /// </summary>
    public static MessageOriginSignature Create(
        IReadOnlyDictionary<string, string?>? verifyKeys,
        IReadOnlyDictionary<string, string?>? previousVerifyKeys = null,
        string? signingIssuer = null,
        string? signingKey = null)
    {
        var verify = new Dictionary<string, List<LockedKey>>(StringComparer.Ordinal);
        AddVerifyKeys(verify, verifyKeys, "VerifyKeys");
        AddVerifyKeys(verify, previousVerifyKeys, "PreviousVerifyKeys");
        LockedKey? signer = null;
        if (!string.IsNullOrWhiteSpace(signingKey))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signingIssuer);
            signer = new LockedKey(ImportPrivate(signingKey));
        }

        return new MessageOriginSignature(
            signer is null ? null : signingIssuer,
            signer,
            verify.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<LockedKey>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>この発行元として署名できるか（その発行元の秘密の鍵を持つ）。</summary>
    public bool CanSign(string issuerId) => _signingKey is not null && string.Equals(_signingIssuer, issuerId, StringComparison.Ordinal);

    /// <summary>この発行元の署名を確かめる公開の鍵があるか。</summary>
    public bool HasVerifyKey(string issuerId) => _verifyKeys.ContainsKey(issuerId);

    /// <summary>署名を作る（自分の秘密の鍵で）。その発行元の秘密の鍵が無ければ null。</summary>
    public string? Sign(Type messageType, Guid messageId, string issuerId, long utcTicks, object message)
    {
        if (!CanSign(issuerId))
            return null;
        var data = Encoding.UTF8.GetBytes(Canonical(Version, messageType, messageId, issuerId, utcTicks, message));
        return $"{Version}.{Convert.ToBase64String(_signingKey!.Sign(data))}";
    }

    /// <summary>署名が合うか（現在の公開の鍵、次に入れ替えの間だけの前の公開の鍵）。鍵が無ければ false。</summary>
    public bool Matches(Type messageType, Guid messageId, string issuerId, long utcTicks, object message, string signature)
    {
        var prefix = Version + ".";
        if (!signature.StartsWith(prefix, StringComparison.Ordinal) || !_verifyKeys.TryGetValue(issuerId, out var keys))
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

        if (presented.Length != SignatureBytes)
            return false;

        var data = Encoding.UTF8.GetBytes(Canonical(Version, messageType, messageId, issuerId, utcTicks, message));
        foreach (var key in keys)
        {
            if (key.Verify(data, presented))
                return true;
        }

        return false;
    }

    /// <summary>正準形（署名の版の）。試験用に公開する。</summary>
    public static string Canonical(Type messageType, Guid messageId, string issuerId, long utcTicks, object message) =>
        Canonical(Version, messageType, messageId, issuerId, utcTicks, message);

    /// <summary>正準形。先頭の版で印の方式を分ける（版をまたいで流用させない）。</summary>
    public static string Canonical(string version, Type messageType, Guid messageId, string issuerId, long utcTicks, object message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        var sb = new StringBuilder();
        sb.Append(version);
        Field(sb, Text(messageType.FullName));
        Field(sb, messageId.ToString("D"));
        Field(sb, Text(issuerId));
        Field(sb, utcTicks.ToString(CultureInfo.InvariantCulture));
        Field(sb, Text(Body(messageType, message)));
        return sb.ToString();
    }

    /// <summary>本文の正規形（固定の設定の JSON）。</summary>
    public static string Body(Type messageType, object message) => JsonSerializer.Serialize(message, messageType, BodyOptions);

    private static void AddVerifyKeys(Dictionary<string, List<LockedKey>> target, IReadOnlyDictionary<string, string?>? source, string name)
    {
        if (source is null)
            return;
        foreach (var (issuer, value) in source)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!target.TryGetValue(issuer, out var list))
                target[issuer] = list = [];
            list.Add(new LockedKey(ImportPublic(value, $"MessageOrigin:{name}:{issuer}")));
        }
    }

    private static ECDsa ImportPrivate(string value)
    {
        const string name = "MessageOrigin:SigningKey";
        var bytes = Decode(value, name);
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(bytes, out var read);
            if (read != bytes.Length)
                throw new CryptographicException();
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"{name} が PKCS#8 の秘密の鍵（P-256）として読めません（公開の鍵を与えていないか確かめてください。値は表示しません）。");
        }

        return RequireP256(key, name);
    }

    private static ECDsa ImportPublic(string value, string name)
    {
        var bytes = Decode(value, name);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length)
                throw new CryptographicException();
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"{name} が公開の鍵（SubjectPublicKeyInfo・P-256）として読めません（受け手に秘密の鍵を渡していないか確かめてください。値は表示しません）。");
        }

        return RequireP256(key, name);
    }

    private static ECDsa RequireP256(ECDsa key, string name)
    {
        if (key.ExportParameters(false).Curve.Oid?.Value != CurveOid)
        {
            key.Dispose();
            throw new InvalidOperationException($"{name} の曲線が P-256 ではありません（値は表示しません）。");
        }

        return key;
    }

    private static byte[] Decode(string value, string name)
    {
        try
        {
            return Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{name} が base64 として読めません（値は表示しません）。");
        }
    }

    // ECDsa の実体はスレッド安全を約していないので、1 つの鍵の操作を直列にする（受け手のハンドラ・送り手は並ぶ）。
    private sealed class LockedKey(ECDsa key)
    {
        private readonly Lock _gate = new();

        public byte[] Sign(byte[] data)
        {
            lock (_gate)
                return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        public bool Verify(byte[] data, byte[] signature)
        {
            try
            {
                lock (_gate)
                    return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    private static void Field(StringBuilder sb, string value) => sb.Append('|').Append(value);

    private static string Text(string? value) =>
        value is null ? "-" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private static JsonSerializerOptions CreateBodyOptions()
    {
        var options = new JsonSerializerOptions
        {
            // 名前は宣言のまま・数値は数値・列挙は数値（JSON の既定）。浮動小数の NaN / ∞ も書けるようにする（例外で落とさない）。
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false,
        };
        options.Converters.Add(new NormalizedDecimalConverter());
        options.Converters.Add(new UtcTicksDateTimeOffsetConverter());
        options.Converters.Add(new TicksDateTimeConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    // 末尾のゼロを落とす（10.50 と 10.5 を同じ値に。注文の承認の印と同じ規則）。
    private sealed class NormalizedDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("正規形は書くだけです。");

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteStringValue((value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
    }

    // 時刻は UTC の ticks（オフセットの表し方に依らない）。
    private sealed class UtcTicksDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("正規形は書くだけです。");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class TicksDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("正規形は書くだけです。");

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(
                (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).Ticks.ToString(CultureInfo.InvariantCulture));
    }
}
