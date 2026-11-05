using System.Security.Cryptography;

namespace AiStockTrading.Shared.Infrastructure.Composable.SecondFactor;

/// <summary>
/// NFR-06, FR-14, #1542, IADR-0615: RFC 6238 の TOTP（HMAC-SHA1・6 桁・30 秒）。認証アプリ（Google Authenticator 等）の既定と同じ形。
/// シークレットは RFC 4648 の base32（パディングなし・大小文字を問わない）。
/// </summary>
public static class Totp
{
    public const int Digits = 6;

    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>時刻 <paramref name="at"/> の時刻の段（Unix 秒 ÷ 30）。</summary>
    public static long StepOf(DateTimeOffset at) => at.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>段 <paramref name="step"/> のコード（6 桁・先頭のゼロを保つ）。</summary>
    public static string Compute(byte[] key, long step)
    {
        ArgumentNullException.ThrowIfNull(key);
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[19] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>base32 を鍵へ写す。書式の外・短すぎる（10 バイト未満）は false。</summary>
    public static bool TryDecodeBase32(string? text, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(text))
            return false;

        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var raw in text.Trim().TrimEnd('='))
        {
            if (raw is ' ' or '-')
                continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw), StringComparison.Ordinal);
            if (index < 0)
                return false;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        if (bytes.Count < 10)
            return false;
        key = [.. bytes];
        return true;
    }
}
