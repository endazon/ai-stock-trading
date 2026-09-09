using AwesomeAssertions;
using OpendAuthGateway.Features.OpendAuth;
using Xunit;

namespace OpendAuthGateway.Tests.Features.OpendAuth;

/// <summary>
/// #722, IADR-0320 決定 3・決定 5: コンソール複製の整形とプロンプト判定。
/// <para>
/// 最重要は<b>コードを画面へ返さないこと</b>である。<c>script</c> は tty の記録なので、
/// 運用者が打った <c>input_phone_verify_code -code=123456</c> が<b>そのまま複製に残る</b>。
/// 伏せ字にしなければ <c>GET /state</c> がコードを echo することになる。
/// </para>
/// </summary>
public class ConsoleTailTests
{
    /// <summary>ESC（0x1B）。属性・定数の中で見分けが付くよう名前を付ける。</summary>
    private const string Esc = "\u001b";

    [Fact]
    public void 投入したコードは伏せ字になる()
    {
        var raw = "Command Tips: input_phone_verify_code -code=<code>\n"
                + "input_phone_verify_code -code=123456\n"
                + "verify failed\n";

        var sanitized = ConsoleTail.Sanitize(raw);

        sanitized.Should().NotContain("123456");
        sanitized.Should().Contain("-code=***");
        sanitized.Should().Contain("input_phone_verify_code", "コマンド名はプロンプト判定に要る");
    }

    [Fact]
    public void ログインパスワードも伏せ字になる()
    {
        ConsoleTail.Sanitize("relogin -login_pwd=5f4dcc3b5aa765d61d8327deb882cf99\n")
            .Should().Be("relogin -login_pwd=***\n");
    }

    [Fact]
    public void ANSIエスケープを取り除く()
    {
        // CSI（画面消去・色）と OSC（端末タイトル。BEL 終端）。どちらも script の記録に必ず混ざる。
        var raw = $"{Esc}[2J{Esc}[H{Esc}[1;32mLogin successful{Esc}[0m\n{Esc}]0;opend\u0007done\n";

        ConsoleTail.Sanitize(raw).Should().Be("Login successful\ndone\n");
    }

    [Fact]
    public void 改行を正規化し制御文字を落とす()
    {
        // CRLF → LF、単独の CR（行内上書き）→ LF、BEL と NUL は落とす。タブは残す。
        var raw = "a\r\nb\rc\u0007d\u0000e\tf\n";

        ConsoleTail.Sanitize(raw).Should().Be("a\nb\ncde\tf\n");
    }

    [Fact]
    public void 空入力は空文字列になる() => ConsoleTail.Sanitize(string.Empty).Should().BeEmpty();

    // ---- プロンプト判定 ---------------------------------------------------------------

    [Theory]
    [InlineData("Command Tips: input_phone_verify_code -code=***\n", "phone")]
    [InlineData("Command Tips: input_pic_verify_code -code=***\n", "pic")]
    [InlineData("Command Tips: req_phone_verify_code\n", "resend")]
    [InlineData("command tips: input_phone_verify_code\n", "phone")]
    public void CommandTips行から待たれている入力を読む(string console, string expected)
    {
        ConsoleTail.ToWireValue(ConsoleTail.DetectPrompt(console)).Should().Be(expected);
    }

    [Fact]
    public void 最後のCommandTips行だけを見る()
    {
        var console = "Command Tips: input_phone_verify_code -code=***\n"
                    + "verify failed\n"
                    + "Command Tips: input_pic_verify_code -code=***\n"
                    + ">>> \n";

        ConsoleTail.ToWireValue(ConsoleTail.DetectPrompt(console)).Should().Be("pic");
    }

    [Fact]
    public void 検証コード以外のCommandTipsなら過去へ遡らない()
    {
        var console = "Command Tips: input_phone_verify_code -code=***\n"
                    + "Login successful\n"
                    + "Command Tips: help\n";

        ConsoleTail.DetectPrompt(console).Should().BeNull(
            "終わった検証を『いま待たれている入力』として返すと、画面が誤った入力欄を出す");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Login successful\n")]
    // Command Tips 行の外にコマンド名があるだけでは判定しない（過去のログ・案内文の混入で誤判定しない）。
    [InlineData("you can use input_phone_verify_code later\n")]
    public void 判定できないときはnullを返す(string console)
        => ConsoleTail.DetectPrompt(console).Should().BeNull();

    [Fact]
    public void 同じ行に複数あるときは入力を求めるものを優先する()
    {
        var console = "Command Tips: input_pic_verify_code -code=*** or req_phone_verify_code\n";

        ConsoleTail.ToWireValue(ConsoleTail.DetectPrompt(console)).Should().Be("pic");
    }

    [Fact]
    public void 伏せ字はプロンプト判定を壊さない()
    {
        var sanitized = ConsoleTail.Sanitize("Command Tips: input_pic_verify_code -code=ab12\n");

        sanitized.Should().NotContain("ab12");
        ConsoleTail.ToWireValue(ConsoleTail.DetectPrompt(sanitized)).Should().Be("pic");
    }
}
