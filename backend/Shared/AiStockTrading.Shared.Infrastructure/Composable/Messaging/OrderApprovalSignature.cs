using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Messaging;

/// <summary>
/// NFR, FR-10, #1527, IADR-0610: 注文の承認（<see cref="OrderApproved"/>）の発行元の印（HMAC-SHA256）を作り、確かめる。
/// <para>
/// 発行元（リスク管理）は承認を発行するときに印を作り、メッセージのヘッダー <see cref="HeaderName"/> に載せる。
/// 受け手（発注執行）は受け取った承認から同じ正準形を組み直し、印を突き合わせる。
/// </para>
/// <para>
/// 🔴 <b>正準形は項目を明示して組む</b>（JSON の直列化には依らない）。直列化の設定・項目の並び・末尾ゼロの扱いが
/// 送り手と受け手で揃っている前提を置かない。<b>契約に項目を足したら、ここにも足す</b>
/// （<c>OrderApprovalSignatureTests</c> が契約の項目の一覧を固定し、足し忘れを赤にする）。
/// </para>
/// </summary>
public sealed class OrderApprovalSignature
{
    /// <summary>印を載せるメッセージヘッダーの名前。</summary>
    public const string HeaderName = "ast-order-approval-sig";

    /// <summary>正準形の版。印の値の先頭に付ける（<c>v1.</c> ＋ base64）。</summary>
    public const string Version = "v1";

    /// <summary>鍵の最小の長さ（バイト）。HMAC-SHA256 の出力長に揃える。</summary>
    public const int MinKeyBytes = 32;

    private readonly byte[] _key;
    private readonly byte[]? _previousKey;

    private OrderApprovalSignature(byte[] key, byte[]? previousKey)
    {
        _key = key;
        _previousKey = previousKey;
    }

    /// <summary>
    /// 鍵（base64）から作る。鍵が空なら null（印を作れない・確かめられない）。
    /// 🔴 鍵が与えられているのに読めない・短いときは例外（起動で止める。黙って印なしへ倒さない）。
    /// 例外の文言に鍵の値を含めない。
    /// </summary>
    public static OrderApprovalSignature? TryCreate(string? keyBase64, string? previousKeyBase64 = null)
    {
        var key = Decode(keyBase64, "Key");
        if (key is null)
            return null;

        return new OrderApprovalSignature(key, Decode(previousKeyBase64, "PreviousKey"));
    }

    /// <summary>承認の印（<c>v1.</c> ＋ base64）。現在の鍵だけで作る。</summary>
    public string Sign(OrderApproved approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        return $"{Version}.{Convert.ToBase64String(Mac(_key, approved))}";
    }

    /// <summary>
    /// 印を確かめる。現在の鍵、次に前の鍵（入れ替えの間だけ与える）で突き合わせる。比較は固定時間で行う。
    /// </summary>
    public OrderApprovalVerification Verify(OrderApproved approved, string? signature)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (string.IsNullOrEmpty(signature))
            return OrderApprovalVerification.Missing;

        var prefix = Version + ".";
        if (!signature.StartsWith(prefix, StringComparison.Ordinal))
            return OrderApprovalVerification.Mismatch;

        byte[] presented;
        try
        {
            presented = Convert.FromBase64String(signature[prefix.Length..]);
        }
        catch (FormatException)
        {
            return OrderApprovalVerification.Mismatch;
        }

        if (CryptographicOperations.FixedTimeEquals(presented, Mac(_key, approved)))
            return OrderApprovalVerification.Valid;

        if (_previousKey is not null && CryptographicOperations.FixedTimeEquals(presented, Mac(_previousKey, approved)))
            return OrderApprovalVerification.Valid;

        return OrderApprovalVerification.Mismatch;
    }

    /// <summary>正準形（試験用に公開する）。項目は <c>|</c> で区切り、文字列は長さを前置して区切りの偽装を防ぐ。</summary>
    public static string Canonical(OrderApproved approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var i = approved.Intent;
        var sb = new StringBuilder();
        sb.Append(Version);
        Field(sb, approved.DecisionId.ToString("D"));
        Field(sb, Text(i.Symbol));
        Field(sb, Num((int)i.Market));
        Field(sb, Num((int)i.Side));
        Field(sb, Num((int)i.ProductType));
        Field(sb, Num((int)i.Mode));
        Field(sb, Num(i.Quantity));
        Field(sb, Dec(i.Price));
        Field(sb, Num((int)i.PositionEffect));
        Field(sb, Dec(i.StopLossPrice));
        Field(sb, Dec(i.FxRateToBase));
        Field(sb, i.MarketOrder ? "1" : "0");
        Field(sb, i.StopFloorSource is { } floor ? Num((int)floor) : "-");
        Field(sb, Dec(i.Atr14));
        Field(sb, i.EntryBasis is { } basis ? Text(basis.Condition) + "/" + Dec(basis.ReferencePrice) : "-");
        Field(sb, Num(approved.ApprovedQuantity));
        Field(sb, approved.ApprovedAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Field(sb, approved.CycleTrigger is { } trigger ? Text(trigger) : "-");
        Field(sb, approved.CycleStartedAt is { } started ? started.UtcTicks.ToString(CultureInfo.InvariantCulture) : "-");
        Field(sb, Num((int)approved.StopLossMethod));
        Field(sb, approved.FromTradeDecision ? "1" : "0");
        Field(sb, Num((int)approved.Origin));
        return sb.ToString();
    }

    private static byte[] Mac(byte[] key, OrderApproved approved) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(approved)));

    private static void Field(StringBuilder sb, string value) => sb.Append('|').Append(value);

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    // 文字列は「長さ:本文」。本文に区切りが含まれても項目の境界はずれない。
    private static string Text(string? value) =>
        value is null ? "-" : $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    // 末尾のゼロを落として正規化する（10.50 と 10.5 を同じ値として扱う。直列化の往復で桁が変わっても印は合う）。
    private static string Dec(decimal? value) =>
        value is { } v ? (v / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : "-";

    private static byte[]? Decode(string? keyBase64, string name)
    {
        if (string.IsNullOrWhiteSpace(keyBase64))
            return null;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyBase64.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"OrderApprovalSigning:{name} が base64 として読めません（値は表示しません）。");
        }

        if (key.Length < MinKeyBytes)
            throw new InvalidOperationException(
                $"OrderApprovalSigning:{name} が短すぎます（{MinKeyBytes} バイト以上の乱数を base64 で与えてください。値は表示しません）。");

        return key;
    }
}

/// <summary>NFR, #1527, IADR-0610: 承認の印の検証の結果。</summary>
public enum OrderApprovalVerification
{
    /// <summary>印が合った。</summary>
    Valid = 0,

    /// <summary>印が無い。</summary>
    Missing = 1,

    /// <summary>印が合わない（形が違う・値が違う）。</summary>
    Mismatch = 2,

    /// <summary>受け手に鍵が無く、確かめられない。</summary>
    Unverifiable = 3,
}

/// <summary>NFR, #1527, IADR-0610: 承認の印の段。</summary>
public enum OrderApprovalSigningMode
{
    /// <summary>段 1（既定）: 印を付け、受け手は確かめて記録するだけで通す。</summary>
    Report = 0,

    /// <summary>段 2: 受け手は印が確かめられない承認を発注しない。鍵が無ければ起動で止まる。</summary>
    Enforce = 1,
}

/// <summary>NFR, #1527, IADR-0610: 構成 <c>OrderApprovalSigning</c>。</summary>
public sealed class OrderApprovalSigningOptions
{
    public const string SectionName = "OrderApprovalSigning";

    /// <summary>段（既定 Report）。</summary>
    public OrderApprovalSigningMode Mode { get; set; } = OrderApprovalSigningMode.Report;

    /// <summary>鍵（base64・32 バイト以上）。Secret から env <c>OrderApprovalSigning__Key</c> で渡す。</summary>
    public string? Key { get; set; }

    /// <summary>入れ替えの間だけ与える前の鍵（検証にだけ使う）。</summary>
    public string? PreviousKey { get; set; }

    /// <summary>
    /// 構成から印の部品を作る。段 2 で鍵が無ければ例外（起動で止める）。段 1 で鍵が無ければ null（印なしで動く）。
    /// </summary>
    public OrderApprovalSignature? Resolve()
    {
        var signature = OrderApprovalSignature.TryCreate(Key, PreviousKey);
        if (signature is null && Mode == OrderApprovalSigningMode.Enforce)
            throw new InvalidOperationException(
                "OrderApprovalSigning:Mode=Enforce には OrderApprovalSigning:Key（Secret）が要ります。");
        return signature;
    }
}
