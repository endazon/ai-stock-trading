using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Logging;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Contracts.Tests;

// 🔴 NFR, #1437, IADR-0571（IADR-0419 と同じ判断）: LLM ゲートウェイの Sent=false の原因の要約（LlmGatewayUnsent.Summarize）の
// 伏せ字は、壁時計の予算を持たない線形時間の照合で行う。旧形は 100 ms のマッチタイムアウトを持ち、CI の並列試験下で
// 数十文字の入力でも RegexMatchTimeoutException が From を突き抜けた（Integration E2E 2026-10-22 21:10Z の実測）。
public class LlmGatewayUnsentRedactionTests
{
    // 型の中の Regex を名前に依らず全部見る（照合を足した・名前を変えたときも、予算の付いた照合が紛れ込めば赤）。
    [Fact]
    public void 要約の伏せ字の照合は壁時計の予算を持たず線形時間である()
    {
        var regexes = typeof(LlmGatewayUnsent)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(Regex))
            .Select(f => (Name: f.Name, Regex: (Regex)f.GetValue(null)!))
            .ToList();

        regexes.Should().NotBeEmpty();
        foreach (var (name, regex) in regexes)
        {
            regex.MatchTimeout.Should().Be(Regex.InfiniteMatchTimeout, $"{name} は壁時計で打ち切らない");
            regex.Options.Should().HaveFlag(RegexOptions.NonBacktracking, $"{name} は入力長に線形の照合で行う");
        }
    }

    // 書き換えた照合は旧パターン（バックトラック・エンジン）と同じ結果を返す（差分）。旧パターンは参照実装としてここだけに残し、
    // 試験内では予算を持たせない（差分の判定を壁時計に依らせない）。伏せる形そのものを意図して変えるときは、
    // この参照実装も同じ変更で改める。
    private static readonly Regex[] ReferencePatterns =
    [
        new(@"(?i)(bearer\s+)[A-Za-z0-9._~+/=\-]+", RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout),
        new(@"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{6,}", RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout),
        new(@"(?i)\b(api[_\-]?key|access[_\-]?token|token|secret|password|authorization)\s*[:=]\s*[^\s,;)」）]+",
            RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout),
        new(@"[A-Za-z0-9_\-+/=]{32,}", RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout),
    ];

    private static readonly string[] ReferenceReplacements = ["$1***", "***", "$1=***", "***"];

    private static string? ReferenceSummarize(string? value, int maxLength = LlmGatewayUnsent.SummaryMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var input = value.Trim();
        if (input.Length > 2000)
            input = input[..2000];
        for (var i = 0; i < ReferencePatterns.Length; i++)
            input = ReferencePatterns[i].Replace(input, ReferenceReplacements[i]);
        return LogSanitizer.Sanitize(input, maxLength);
    }

    public static TheoryData<string> Samples() =>
    [
        "upstream failed: Authorization: Bearer abc.def.ghi api_key=zzz-secret-123 sk-ant-api03-ABCDEFGHIJ",
        "429 Too Many Requests",
        "BEARER   x.y.z, token=abc; secret: s3cr3t) password=「p」 access-token=q accesstoken:r",
        "pk-123456 rk-abcdef sk-12345 xsk-1234567 sk-_-_-_-_",
        new string('a', 31) + " " + new string('b', 32) + " " + new string('c', 2100),
        "api_key=" + new string('k', 40) + " Authorization=Bearer " + new string('t', 40),
        "token=トークン値、secret=値）password=値」",
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void 書き換えた照合は旧パターンと同じ要約を返す_代表例(string input)
    {
        LlmGatewayUnsent.Summarize(input).Should().Be(ReferenceSummarize(input));
        LlmGatewayUnsent.Summarize(input, int.MaxValue).Should().Be(ReferenceSummarize(input, int.MaxValue));
    }

    // 伏せる形の境界（区切り・大文字小文字・長さの閾値）に寄せた字母から作る擬似乱数の入力で、旧パターンと突き合わせる。
    [Fact]
    public void 書き換えた照合は旧パターンと同じ要約を返す_擬似乱数の入力()
    {
        string[] pieces =
        [
            "a", "Z", "0", "-", "_", "+", "/", "=", ".", "~", " ", "\t", ",", ";", ")", "」", "）", ":", "長",
            "bearer ", "Bearer\t", "sk-", "pk-", "rk-", "api_key", "api-key", "apikey", "access_token", "token",
            "secret", "password", "Authorization", " = ", ": ",
        ];
        var random = new Random(1437);
        var mismatches = new List<string>();
        for (var n = 0; n < 20_000; n++)
        {
            var sb = new StringBuilder();
            var count = random.Next(1, 60);
            for (var i = 0; i < count; i++)
            {
                var piece = pieces[random.Next(pieces.Length)];
                sb.Append(random.Next(8) == 0 ? new string(piece[0], random.Next(1, 40)) : piece);
            }

            var input = sb.ToString();
            if (LlmGatewayUnsent.Summarize(input, int.MaxValue) != ReferenceSummarize(input, int.MaxValue))
                mismatches.Add(input);
        }

        mismatches.Should().BeEmpty();
    }
}
