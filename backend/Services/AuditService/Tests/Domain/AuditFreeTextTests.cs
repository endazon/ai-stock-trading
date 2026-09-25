using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuditService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests;

// FR-10, FR-11, NFR（セキュリティ・NFR-10 の 7 年保持）, #842, IADR-0405 決定2:
// 監査台帳の自由記述欄へ入る理由文の**上限**と**線引き**（接続先を伏せる）。
// 境界値（T-10-852）・否定形（T-10-851 / T-10-853 の誤検出側）・プロパティ（T-10-853）の 3 点セット。
// #984, IADR-0419: 伏せ字の照合は線形時間で壁時計の予算を持たない（T-10-981）。書き換えた形が旧パターンと同じ意味で
// あること（T-10-980）と、長い入力でも結果が入力だけで決まること（T-10-982）を固定する。
public class AuditFreeTextTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 25, 2, 0, 0, TimeSpan.Zero);

    // #842 の実測文面（MMApiMoomooTradeClient が接続確立の失敗で投げる）。
    private const string MeasuredConnectFailure =
        "OpenD への InitConnect が失敗しました（opend.ai-stock-trading.svc:11111）。";

    private static AlternativeProtectiveStopAttempted Attempted(string? reason) => new(
        Guid.NewGuid(), Guid.NewGuid(), "AAPL", Market.UnitedStates,
        AlternativeProtectiveOrderType.StopLimit, OrderStatus.Rejected, BrokerOrderId: null,
        RejectReasonCode: null, RejectReasonMessage: reason,
        StopLossExecutionMethod.AlternativeBrokerOrderType, BrokerProvider.MoomooSimulate, OccurredAt);

    private static string? DetailReason(AuditEntry entry) =>
        JsonSerializer.Deserialize<AlternativeProtectiveStopAttempted>(entry.Detail, AuditDetailJson.Options)!
            .RejectReasonMessage;

    // ---- T-10-851: 実測の接続失敗文面が台帳（要約・全量 JSON）に接続先を残さない（否定形） ----

    [Fact]
    public void 代替レグの理由文に接続先があっても台帳には残さない_否定形()
    {
        var entry = AuditEntryFactory.From(Attempted(MeasuredConnectFailure), Id, RecordedAt);

        entry.Summary.Should().NotContain("opend.ai-stock-trading.svc").And.NotContain("11111");
        entry.Detail.Should().NotContain("opend.ai-stock-trading.svc").And.NotContain("11111");
        // 何が起きたか（接続確立の失敗）は読める——伏せるのは接続先だけである。
        DetailReason(entry).Should().Be("OpenD への InitConnect が失敗しました（［接続先］）。");
        entry.Summary.Should().Contain("InitConnect が失敗");
    }

    // ---- T-10-852: 長さの境界値 ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(AuditFreeText.MaxLength - 1)]
    [InlineData(AuditFreeText.MaxLength)]
    public void 上限以内の理由文は変えない_境界値(int length)
    {
        var text = new string('あ', length);

        AuditFreeText.Sanitize(text).Should().Be(text);
    }

    [Theory]
    [InlineData(AuditFreeText.MaxLength + 1)]
    [InlineData(5_000)]
    public void 上限を超える理由文は上限で切り詰めて省略記号を付ける_境界値(int length)
    {
        var text = new string('あ', length);

        AuditFreeText.Sanitize(text).Should().Be(new string('あ', AuditFreeText.MaxLength) + "…");
    }

    [Fact]
    public void 切り詰めはサロゲートペアを割らない()
    {
        // 上限の直前 1 文字をサロゲートペアの前半に置く（𠮷 = U+20BB7）。
        var text = new string('a', AuditFreeText.MaxLength - 1) + "𠮷" + new string('b', 10);

        var sanitized = AuditFreeText.Sanitize(text)!;

        sanitized.Should().Be(new string('a', AuditFreeText.MaxLength - 1) + "…");
        sanitized.Any(char.IsSurrogate).Should().BeFalse();
    }

    [Fact]
    public void null_は_null_のまま()
    {
        AuditFreeText.Sanitize(null).Should().BeNull();
    }

    [Fact]
    public void 代替レグの理由文が長くても台帳の全量JSONは上限を超えない()
    {
        var entry = AuditEntryFactory.From(Attempted(new string('x', 10_000)), Id, RecordedAt);

        DetailReason(entry)!.Length.Should().Be(AuditFreeText.MaxLength + 1, "上限＋省略記号 1 文字");
        entry.Detail.Length.Should().BeLessThan(2_000, "全量 JSON に 10,000 文字がそのまま入らない");
    }

    // ---- T-10-853: 接続先の形を伏せる／通常の理由文は変えない ----

    [Theory]
    [InlineData("接続失敗（opend.ai-stock-trading.svc:11111）", "接続失敗（［接続先］）")]
    [InlineData("connect to 127.0.0.1:11111 failed", "connect to ［接続先］ failed")]
    [InlineData("接続先127.0.0.1で失敗", "接続先［接続先］で失敗")]
    [InlineData("peer 10.0.0.5 closed.", "peer ［接続先］ closed.")]
    [InlineData("see https://opend.example.com:8443/api?x=1 for details", "see ［接続先］ for details")]
    [InlineData("host [::1]:11111 refused", "host ［接続先］ refused")]
    [InlineData("localhost:11111 refused", "［接続先］ refused")]
    [InlineData("OPEND.CLUSTER.LOCAL:1 down", "［接続先］ down")]
    // T-10-982（#984）: 隣り合う接続先は両方伏せる（前の接続先の最後の文字・直後の区切りが次の境界を兼ねる）。
    [InlineData("[::1]1.2.3.4", "［接続先］［接続先］")]
    [InlineData("1.1.1.1 2.2.2.2", "［接続先］ ［接続先］")]
    [InlineData("a.example:1,b.example:2", "［接続先］,［接続先］")]
    public void 接続先を伏せる(string input, string expected)
    {
        AuditFreeText.Sanitize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Paper trading does not support StopLimit order")]
    [InlineData("The precision of Price in Place Order does not meet the specification.")]
    [InlineData("発注前検証で棄却しました（種別=TrailingStop 数量=10 価格=329.03 発火価格=332.35 トレール幅=0）。OpenD へは送信していません。")]
    [InlineData("moomoo PlaceOrder が失敗しました（retType=-1）: 10:30 以降は受け付けません")]
    [InlineData("数量 [1] の注文は受け付けられません")]
    [InlineData("SDK の返信待ちがタイムアウト（12 秒）")]
    public void 通常の理由文は変えない_誤検出の否定形(string input)
    {
        AuditFreeText.Sanitize(input).Should().Be(input);
    }

    // ---- T-10-982（#984 / IADR-0419）: 長い反復入力でも理由文を捨てず、結果は入力だけで決まる ----
    // 是正前は 200 ms の壁時計の予算で打ち切り、高負荷では正当な入力でも定型文へ置き換わった（#984）。
    // 表明は「打ち切られなかった」ではなく期待値との一致である（CPU の混み具合で結果が変わらないことの表明）。
    [Theory]
    [InlineData("x", false)]
    [InlineData("a.", false)]
    [InlineData("1.", false)]
    [InlineData("a-", false)]
    [InlineData("ab://", true)] // 反復全体が 1 つの URL（`ab://ab://…`）であり、1 つの伏せ字になる
    [InlineData("[f", false)]
    public void 長い反復入力でも伏せ字の処理は打ち切られない(string unit, bool wholeIsEndpoint)
    {
        var input = string.Concat(Enumerable.Repeat(unit, 20_000 / unit.Length));

        var sanitized = AuditFreeText.Sanitize(input);

        sanitized.Should().Be(wholeIsEndpoint
            ? AuditFreeText.EndpointPlaceholder
            : input[..AuditFreeText.MaxLength] + "…");
    }

    // ---- T-10-981（#984 / IADR-0419）: 伏せ字の照合は壁時計の予算を持たず、線形時間の照合である ----
    // 型の中の Regex を名前に依らず全部見る（照合を足した・名前を変えたときも、予算の付いた照合が紛れ込めば赤）。
    [Fact]
    public void 伏せ字の照合は壁時計の予算を持たず線形時間である()
    {
        var regexes = typeof(AuditFreeText)
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

    // ---- T-10-980（#984 / IADR-0419）: 書き換えた形は旧パターン（IADR-0405 決定2）と同じ結果を返す（差分） ----
    // 旧パターン（後読み・先読み・原子グループ）は参照実装としてここだけに残す。試験内では予算を持たせない
    // （旧パターンも実測で線形であり、差分の判定を壁時計に依らせない）。伏せる形そのものを意図して変えるときは、
    // この参照実装も同じ変更で改める。
    private static readonly Regex ReferenceEndpoint = new(
        @"(?<![A-Za-z0-9+.\-])[A-Za-z](?>[A-Za-z0-9+.\-]*)://[^\s（）()「」<>""']+"
        + @"|\[[0-9A-Fa-f.]*:[0-9A-Fa-f:.]*\](?::\d{1,5})?"
        + @"|(?<![A-Za-z0-9_.\-])\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?(?![A-Za-z0-9_]|\.\d)"
        + @"|(?<![A-Za-z0-9_.\-])(?=[A-Za-z0-9.\-]*[A-Za-z])(?>[A-Za-z0-9\-]+)(?:\.(?>[A-Za-z0-9\-]+))+:\d{1,5}(?!\d)"
        + @"|(?<![A-Za-z0-9_.\-])localhost:\d{1,5}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        Regex.InfiniteMatchTimeout);

    private static readonly Regex ControlRun = new(@"\p{Cc}+", RegexOptions.None, Regex.InfiniteMatchTimeout);

    [Fact]
    public void 伏せ字は旧パターンと同じ結果を返す_差分()
    {
        // 字母は境界になる記号（区切り・語の中の記号）・全角数字（\d に入る）・大文字小文字の等価文字（K の Kelvin 記号等）を含む。
        string[] alphabets =
        [
            "ab1.:-/[]x ", "aZ09.:/[]-_+ （）\"'<>「」", "1.:2 3a", "lh.:1[]f/ ", "localhost:1.2 ",
            "abcXYZ019 .:-/（）。、あいう接続失敗\n\t", "１２.:a[] \u212Ak\u0130\u017F", "a.b:12345678 [f::]",
        ];
        // 接続先の形と、その断片（境界の兼用・長すぎるポート・閉じない括弧）を部品として混ぜる。
        string[] parts =
        [
            "localhost:1", "127.0.0.1", "a.b:1", "http://x", "[::1]:2", "1.2.3.4:5", "opend.svc:11111", "[f", "a://",
            "1.2.3.4:123456", "x.y:123456",
        ];
        var random = new Random(984);

        for (var i = 0; i < 20_000; i++)
        {
            var alphabet = alphabets[random.Next(alphabets.Length)];
            var builder = new StringBuilder();
            var length = random.Next(0, 25);
            for (var k = 0; k < length; k++)
            {
                if (random.Next(6) == 0)
                    builder.Append(parts[random.Next(parts.Length)]);
                else
                    builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            var input = builder.ToString();
            // 上限（500 文字）に届かない長さに収めてあるので、切り詰めは比べる対象に入らない。
            var expected = ControlRun.Replace(ReferenceEndpoint.Replace(input, AuditFreeText.EndpointPlaceholder), " ");

            AuditFreeText.Sanitize(input).Should().Be(expected, $"入力 #{i}: [{input}]");
        }
    }

    [Fact]
    public void 改行などの制御文字は空白1つへ畳む()
    {
        AuditFreeText.Sanitize("一行目\r\n\t二行目").Should().Be("一行目 二行目");
    }

    // プロパティ: 任意の文面（接続先を任意位置に埋め込む）で、長さ上限と「接続先が残らない」が常に成り立つ。
    [Fact]
    public void 任意の入力で上限と伏せ字が成り立つ_プロパティ()
    {
        var random = new Random(842);
        const string alphabet = "abcXYZ019 .:-/（）。、あいう接続失敗\n";
        string[] endpoints =
        [
            "opend.ai-stock-trading.svc:11111", "127.0.0.1:11111", "192.168.10.20", "[fe80::1]:443",
            "https://opend.example.com/x", "localhost:8080",
        ];

        for (var i = 0; i < 500; i++)
        {
            var endpoint = endpoints[random.Next(endpoints.Length)];
            var prefix = RandomText(random, alphabet, random.Next(0, 800));
            var suffix = RandomText(random, alphabet, random.Next(0, 800));
            // 接続先の前後は ASCII 英数と連結しない（連結した語は別の語であり、伏せる対象の形ではない）。
            var input = prefix + " " + endpoint + " " + suffix;

            var sanitized = AuditFreeText.Sanitize(input)!;

            sanitized.Length.Should().BeLessThanOrEqualTo(AuditFreeText.MaxLength + 1);
            sanitized.Should().NotContain(endpoint, $"入力 #{i}: {input}");
            sanitized.Any(char.IsControl).Should().BeFalse();
        }
    }

    private static string RandomText(Random random, string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }
}
