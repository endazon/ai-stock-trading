using AwesomeAssertions;
using OpendAuthGateway.Features.OpendAuth;
using Xunit;

namespace OpendAuthGateway.Tests.Features.OpendAuth;

/// <summary>
/// #722, IADR-0320 決定 1: <b>OpenD のコンソールへ書ける行は 3 つだけ</b>という不変条件を固定する。
/// <para>
/// ここが破れると、<c>show_delay_report -detail_report_path=&lt;path&gt;</c>（root 権限で任意パスへ
/// ファイルを書く）や <c>relogin -login_pwd=</c> が 1 行で通ってしまう。
/// <b>実口座に対する重大な事故</b>であり、この試験群が本 PR の中核である。
/// </para>
/// </summary>
public class OpendConsoleCommandTests
{
    // ---- 肯定形: 受理される 3 つの形 --------------------------------------------------

    [Theory]
    [InlineData("phone", "1234", "input_phone_verify_code -code=1234\n")]
    [InlineData("phone", "123456", "input_phone_verify_code -code=123456\n")]
    [InlineData("phone", "12345678", "input_phone_verify_code -code=12345678\n")]
    [InlineData("pic", "ab12", "input_pic_verify_code -code=ab12\n")]
    [InlineData("pic", "ABCD", "input_pic_verify_code -code=ABCD\n")]
    [InlineData("pic", "0000", "input_pic_verify_code -code=0000\n")]
    public void 正しい入力はサーバが組み立てた1行になる(string kind, string code, string expected)
    {
        var accepted = OpendConsoleCommand.TryCompose(kind, code, out var line, out var rejection);

        accepted.Should().BeTrue();
        rejection.Should().Be(VerifyRejection.None);
        line.Should().Be(expected);
    }

    [Fact]
    public void 再送はコードを取らず引数なしのコマンドになる()
    {
        OpendConsoleCommand.TryCompose("resend", null, out var line, out var rejection).Should().BeTrue();

        rejection.Should().Be(VerifyRejection.None);
        line.Should().Be("req_phone_verify_code\n");
    }

    [Fact]
    public void 再送は空文字列のコードも受け付ける()
    {
        OpendConsoleCommand.TryCompose("resend", string.Empty, out var line, out _).Should().BeTrue();

        line.Should().Be("req_phone_verify_code\n");
    }

    // ---- 否定形: kind は閉じた列挙であり、コマンド文字列は決して通らない ----------------

    [Theory]
    // OpenD のコンソールが実際に受け付ける「危険な側」のコマンド。1 つでも通れば事故になる。
    [InlineData("relogin")]
    [InlineData("relogin -login_pwd=hunter2")]
    [InlineData("exit")]
    [InlineData("quit")]
    [InlineData("close_api_conn")]
    [InlineData("set_log_level")]
    [InlineData("show_delay_report -detail_report_path=/root/.com.moomoo.OpenD/F3CNN/Device.dat")]
    [InlineData("show_sub_info -sub_info_path=/opt/opend/OpenD.xml")]
    // 「それらしい」綴りも通さない（大文字小文字・前後空白・部分一致）。
    [InlineData("Phone")]
    [InlineData("PHONE")]
    [InlineData(" phone")]
    [InlineData("phone ")]
    [InlineData("phone2")]
    [InlineData("input_phone_verify_code")]
    [InlineData("")]
    [InlineData(null)]
    public void 閉じた列挙以外のkindは組み立てずに棄却する(string? kind)
    {
        var accepted = OpendConsoleCommand.TryCompose(kind, "1234", out var line, out var rejection);

        accepted.Should().BeFalse();
        rejection.Should().Be(VerifyRejection.UnknownKind);
        line.Should().BeEmpty("棄却したときは呼び出し側が書くべき行が存在してはならない");
    }

    // ---- 否定形: コードの形（アンカー付き全一致） --------------------------------------

    [Theory]
    // 改行注入。**ここが本命**である。`^…$` では "123456\n" が通ってしまう（.NET の $ は末尾の \n の前にも一致する）。
    [InlineData("123456\n")]
    [InlineData("123456\r")]
    [InlineData("123456\r\n")]
    [InlineData("123456\nexit")]
    [InlineData("123456\nshow_delay_report -detail_report_path=/opt/opend/OpenD.xml")]
    [InlineData("\n123456")]
    // NUL。
    [InlineData("1234\0")]
    [InlineData("\0" + "1234")]
    // 前後の空白（tab / 半角空白）。
    [InlineData(" 1234")]
    [InlineData("1234 ")]
    [InlineData("\t1234")]
    // 非 ASCII 数字。`\d` を使っていれば通ってしまう。
    [InlineData("１２３４")]
    [InlineData("١٢٣٤")]
    [InlineData("१२३४")]
    // 桁数不足・文字種違反。
    [InlineData("123")]
    [InlineData("12a4")]
    [InlineData("12-4")]
    [InlineData("1234;")]
    public void 電話コードは壊れていれば組み立てずに棄却する(string code)
    {
        var accepted = OpendConsoleCommand.TryCompose("phone", code, out var line, out var rejection);

        accepted.Should().BeFalse();
        rejection.Should().BeOneOf(VerifyRejection.MalformedCode, VerifyRejection.CodeTooLong);
        line.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ab1")]
    [InlineData("ab123")]
    [InlineData("ab1\n")]
    [InlineData("ab1 ")]
    [InlineData("ab-1")]
    [InlineData("ａｂ１２")]
    [InlineData("ab1\0")]
    public void 画像コードは壊れていれば組み立てずに棄却する(string code)
    {
        var accepted = OpendConsoleCommand.TryCompose("pic", code, out var line, out var rejection);

        accepted.Should().BeFalse();
        rejection.Should().BeOneOf(VerifyRejection.MalformedCode, VerifyRejection.CodeTooLong);
        line.Should().BeEmpty();
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("pic")]
    public void コードが要る種別で空なら棄却する(string kind)
    {
        OpendConsoleCommand.TryCompose(kind, null, out var line, out var rejection).Should().BeFalse();

        rejection.Should().Be(VerifyRejection.MissingCode);
        line.Should().BeEmpty();
    }

    [Fact]
    public void 長すぎる入力は照合に掛ける前に落とす()
    {
        var overlong = new string('1', 4096);

        OpendConsoleCommand.TryCompose("phone", overlong, out var line, out var rejection).Should().BeFalse();

        rejection.Should().Be(VerifyRejection.CodeTooLong);
        line.Should().BeEmpty();
    }

    [Fact]
    public void 再送にコードを付けると棄却する()
    {
        OpendConsoleCommand.TryCompose("resend", "1234", out var line, out var rejection).Should().BeFalse();

        rejection.Should().Be(VerifyRejection.UnexpectedCode);
        line.Should().BeEmpty();
    }

    // ---- 全域: 出力は必ず 3 つの形のいずれかである -------------------------------------

    /// <summary>
    /// 🔴 <b>網羅の型で押さえる。</b> 個別の否定形は「思い付いた形」しか塞げないので、
    /// <b>受理された場合の出力が 3 つの形のどれかであること</b>を全ケースで確かめる。
    /// これが通る限り、どんな入力を与えても 4 つ目の行は生まれない。
    /// </summary>
    [Fact]
    public void 受理された出力は常に3つの形のいずれかである()
    {
        string[] kinds = ["phone", "pic", "resend", "PHONE", "relogin", "", "  ", "phone\n"];
        string?[] codes =
        [
            null, "", " ", "1234", "12345678", "123456789", "abcd", "ab12", "AB12",
            "1234\n", "1234\r\nexit", "1234\0", "１２３４", "-code=1", "a b", "'; exit",
            "$(id)", "`id`", "1234;show_sub_info -sub_info_path=/tmp/x", new string('9', 100),
        ];

        var produced = new List<string>();
        foreach (var kind in kinds)
        {
            foreach (var code in codes)
            {
                if (OpendConsoleCommand.TryCompose(kind, code, out var line, out _)) produced.Add(line);
                else line.Should().BeEmpty("棄却したときに行が残っていてはならない");
            }
        }

        produced.Should().NotBeEmpty("肯定形が 1 件も無いと、この試験は空振りでも緑になる");
        produced.Should().OnlyContain(l => IsAllowedLine(l));
    }

    /// <summary>allowlist の定義そのもの（試験側で独立に書く）。</summary>
    private static bool IsAllowedLine(string line)
    {
        if (!line.EndsWith('\n')) return false;
        if (line.Count(c => c == '\n') != 1) return false;

        var body = line[..^1];
        if (body == "req_phone_verify_code") return true;
        if (body.StartsWith("input_phone_verify_code -code=", StringComparison.Ordinal))
        {
            var code = body["input_phone_verify_code -code=".Length..];
            return code.Length is >= 4 and <= 8 && code.All(char.IsAsciiDigit);
        }

        if (body.StartsWith("input_pic_verify_code -code=", StringComparison.Ordinal))
        {
            var code = body["input_pic_verify_code -code=".Length..];
            return code.Length == 4 && code.All(char.IsAsciiLetterOrDigit);
        }

        return false;
    }

    /// <summary>否定形（判定そのものが load-bearing であること）: allowlist の定義が何でも通していない。</summary>
    [Theory]
    [InlineData("exit\n")]
    [InlineData("input_phone_verify_code -code=123\n")]
    [InlineData("input_phone_verify_code -code=123456\ninput_phone_verify_code -code=999999\n")]
    [InlineData("input_pic_verify_code -code=ab-1\n")]
    [InlineData("req_phone_verify_code")]
    [InlineData("show_delay_report -detail_report_path=/x\n")]
    public void allowlistの定義は許されない行を弾く(string line) => IsAllowedLine(line).Should().BeFalse();
}
