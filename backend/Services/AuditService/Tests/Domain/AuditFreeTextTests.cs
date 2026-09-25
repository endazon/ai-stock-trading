using System.Text.Json;
using AuditService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace AuditService.Tests;

// FR-10, FR-11, NFR（セキュリティ・NFR-10 の 7 年保持）, #842, IADR-0405 決定2:
// 監査台帳の自由記述欄へ入る理由文の**上限**と**線引き**（接続先を伏せる）。
// 境界値（T-10-852）・否定形（T-10-851 / T-10-853 の誤検出側）・プロパティ（T-10-853）の 3 点セット。
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

    // 任意長の例外メッセージで正規表現がタイムアウトし、理由文ごと失われないこと（バックトラックの 2 乗化の回帰防止）。
    [Theory]
    [InlineData("x")]
    [InlineData("a.")]
    [InlineData("1.")]
    [InlineData("a-")]
    [InlineData("ab://")]
    [InlineData("[f")]
    public void 長い反復入力でも伏せ字の処理は打ち切られない(string unit)
    {
        var input = string.Concat(Enumerable.Repeat(unit, 20_000 / unit.Length));

        var sanitized = AuditFreeText.Sanitize(input)!;

        sanitized.Should().NotContain("整形できなかった");
        sanitized.Length.Should().BeLessThanOrEqualTo(AuditFreeText.MaxLength + 1);
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
