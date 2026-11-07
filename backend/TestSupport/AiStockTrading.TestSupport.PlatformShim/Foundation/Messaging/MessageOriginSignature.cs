using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;

/// <summary>
/// 🔴 NFR, FR-10, IADR-0627 決定 2: 統制の入力になるメッセージの発行元の印（HMAC-SHA256。発行元のサービスごとの鍵）。
/// <para>
/// 印の対象は <b>型・メッセージ ID・発行元・時刻・本文の正規形</b>である（<see cref="Canonical"/>）。
/// 文字列は長さを前置して区切りの偽装を防ぐ。本文の正規形は固定の設定の JSON で、decimal の末尾のゼロ・時刻のオフセットを
/// 正規化する（Wolverine の直列化の往復で値の表し方が変わっても印は合う。往復は試験で型ごとに固定する）。
/// </para>
/// <para>鍵の値は例外・ログに出さない。</para>
/// </summary>
public sealed class MessageOriginSignature
{
    /// <summary>発行元の識別子（例 <c>trade-decision</c>）。</summary>
    public const string IssuerHeader = "ast-origin";

    /// <summary>メッセージ ID（Guid）。再送の検出に使う。</summary>
    public const string IdHeader = "ast-origin-id";

    /// <summary>発行の時刻（UTC の ticks）。</summary>
    public const string TimeHeader = "ast-origin-at";

    /// <summary>印（<c>v1.</c> ＋ base64）。</summary>
    public const string SignatureHeader = "ast-origin-sig";

    /// <summary>正準形の版。</summary>
    public const string Version = "v1";

    /// <summary>鍵の最小の長さ（バイト）。</summary>
    public const int MinKeyBytes = 32;

    private static readonly JsonSerializerOptions BodyOptions = CreateBodyOptions();

    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly IReadOnlyDictionary<string, byte[]> _previousKeys;

    private MessageOriginSignature(IReadOnlyDictionary<string, byte[]> keys, IReadOnlyDictionary<string, byte[]> previousKeys)
    {
        _keys = keys;
        _previousKeys = previousKeys;
    }

    /// <summary>
    /// 発行元ごとの鍵（base64）から作る。空の値は「鍵なし」として扱う。
    /// 🔴 与えられているのに読めない・短い鍵は例外（起動で止める。黙って印なしへ倒さない）。
    /// </summary>
    public static MessageOriginSignature Create(
        IReadOnlyDictionary<string, string?>? keys, IReadOnlyDictionary<string, string?>? previousKeys = null) =>
        new(DecodeAll(keys, "Keys"), DecodeAll(previousKeys, "PreviousKeys"));

    /// <summary>この発行元の鍵があるか（印を作れる・確かめられる）。</summary>
    public bool HasKey(string issuerId) => _keys.ContainsKey(issuerId);

    /// <summary>印を作る（現在の鍵だけで）。鍵が無ければ null。</summary>
    public string? Sign(Type messageType, Guid messageId, string issuerId, long utcTicks, object message)
    {
        if (!_keys.TryGetValue(issuerId, out var key))
            return null;
        return $"{Version}.{Convert.ToBase64String(Mac(key, messageType, messageId, issuerId, utcTicks, message))}";
    }

    /// <summary>印が合うか（現在の鍵、次に前の鍵）。比較は固定時間。鍵が無ければ false。</summary>
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

    /// <summary>正準形（試験用に公開する）。</summary>
    public static string Canonical(Type messageType, Guid messageId, string issuerId, long utcTicks, object message)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(message);
        var sb = new StringBuilder();
        sb.Append(Version);
        Field(sb, Text(messageType.FullName));
        Field(sb, messageId.ToString("D"));
        Field(sb, Text(issuerId));
        Field(sb, utcTicks.ToString(CultureInfo.InvariantCulture));
        Field(sb, Text(Body(messageType, message)));
        return sb.ToString();
    }

    /// <summary>本文の正規形（固定の設定の JSON）。</summary>
    public static string Body(Type messageType, object message) => JsonSerializer.Serialize(message, messageType, BodyOptions);

    private static byte[] Mac(byte[] key, Type messageType, Guid messageId, string issuerId, long utcTicks, object message) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(messageType, messageId, issuerId, utcTicks, message)));

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
