using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using OpendAuthGateway.Infrastructure;
using Xunit;

namespace OpendAuthGateway.Tests;

/// <summary>
/// #722, ADR-0002, IADR-0053, IADR-0322: サイドカーの HTTP 面の受け入れ試験。
/// <para>
/// 安全要件は<b>両向き</b>で押さえる —— 正しい入力が通ること、壊れた入力ごとに 400 になり
/// <b>OpenD の標準入力へ 1 バイトも書かれない</b>こと。
/// </para>
/// </summary>
public class OpendAuthEndpointsTests : IDisposable
{
    // 🔴 ホストは**試験ごとに新しく**作る。流量制限とコンソール複製は singleton / 共有ファイルであり、
    // 使い回すと「先に走った試験が枠を使い切っていたから落ちた」という順序依存の赤が出る。
    private readonly OpendAuthWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public OpendAuthEndpointsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<HttpResponseMessage> PostAsync(string json)
        => _client.PostAsync("/opend-auth/verify", new StringContent(json, Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> PostResendAsync()
        => _client.PostAsync("/opend-auth/resend", new StringContent(string.Empty));

    // ---- コンソールの状態（**どのコマンドを送るかはここだけが決める**） -------------------
    //
    // 🔴 planning#594 の裁定により、種別は要求本文ではなく**待機中のプロンプト**から決まる。
    // したがって肯定形の試験はすべて、先にプロンプトを置いてから投げる。

    private const string WaitingForPhone = ">>>Logging in\n>>>Command Tips: input_phone_verify_code -code=123456\n";
    private const string WaitingForPic = ">>>Logging in\n>>>Command Tips: input_pic_verify_code -code=abcd\n";
    private const string NotWaiting = ">>>moomoo OpenD is running\n>>>Login successful\n";

    // ---- POST /opend-auth/verify: 肯定形 -----------------------------------------------

    [Theory]
    [InlineData(WaitingForPhone, "123456", "input_phone_verify_code -code=123456\n")]
    [InlineData(WaitingForPic, "ab12", "input_pic_verify_code -code=ab12\n")]
    public async Task 待機中のプロンプトからサーバが組み立てた1行だけを書く(string console, string code, string expected)
    {
        _factory.GivenConsole(console);

        var response = await PostAsync($$"""{"code":"{{code}}"}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Writer.Lines.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public async Task 再送は引数なしのコマンドを書く()
    {
        _factory.GivenConsole(WaitingForPhone);

        var response = await PostResendAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Writer.Lines.Should().ContainSingle().Which.Should().Be("req_phone_verify_code\n");
    }

    [Fact]
    public async Task 応答に投入したコードを含めない()
    {
        _factory.GivenConsole(WaitingForPhone);

        var response = await PostAsync("""{"code":"987654"}""");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("987654", "一度きりのコードを応答へ echo しない（IADR-0322 決定 5）");
        body.Should().Contain("accepted");
    }

    // ---- 🔴 クライアントはコマンドを選べない（planning#594 の裁定の中心） ------------------

    [Theory]
    // 要求本文へ種別やコマンドを紛れ込ませても、**送られる行は待機中のプロンプトが決める**。
    [InlineData("""{"code":"123456","kind":"pic"}""")]
    [InlineData("""{"code":"123456","kind":"resend"}""")]
    [InlineData("""{"code":"123456","kind":"relogin -login_pwd=x"}""")]
    [InlineData("""{"code":"123456","command":"exit"}""")]
    [InlineData("""{"code":"123456","kind":"show_delay_report -detail_report_path=/opt/opend/OpenD.xml"}""")]
    public async Task 要求本文の余計な欄は送るコマンドを変えられない(string json)
    {
        _factory.GivenConsole(WaitingForPhone);

        var response = await PostAsync(json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Writer.Lines.Should().ContainSingle().Which.Should()
            .Be("input_phone_verify_code -code=123456\n",
                "送るコマンドは待機中のプロンプトだけが決める（要求本文は影響しない）");
    }

    // ---- 入力待ちでないとき・状態が取れないとき（**この 2 つを混ぜない**） ----------------

    [Fact]
    public async Task 入力待ちでなければ409で何も書かない()
    {
        _factory.GivenConsole(NotWaiting);

        var response = await PostAsync("""{"code":"123456"}""");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not_waiting");
        _factory.Writer.Lines.Should().BeEmpty(
            "入力待ちでないときに書くと、次のプロンプトで消費されて 1 回を食う");
    }

    [Fact]
    public async Task 状態を取得できなければ503で何も書かない()
    {
        // コンソールの複製を置かない＝**供給が無い**。「入力待ちでない（対象なし）」とは別物である。
        var response = await PostAsync("""{"code":"123456"}""");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("console_unavailable");
        _factory.Writer.Lines.Should().BeEmpty();
    }

    // ---- POST /opend-auth/verify: 否定形（すべて「何も書かれない」ことまで見る） ----------

    [Theory]
    // 制御文字による行の注入。**1 つでも通れば実口座への事故**になる。
    [InlineData("""{"code":"123456\nexit"}""")]
    [InlineData("""{"code":"123456\r\nshow_sub_info -sub_info_path=/tmp/x"}""")]
    [InlineData("""{"code":"123456 "}""")]
    // 文字種・桁数・前後空白・非 ASCII 数字。
    [InlineData("""{"code":"12"}""")]
    [InlineData("""{"code":"123456789"}""")]
    [InlineData("""{"code":" 123456"}""")]
    [InlineData("""{"code":"12345６"}""")]
    // コードの欠落。
    [InlineData("""{}""")]
    [InlineData("""{"code":null}""")]
    [InlineData("""{"code":""}""")]
    public async Task 壊れた投入は400で何も書かない(string json)
    {
        _factory.GivenConsole(WaitingForPhone);

        var response = await PostAsync(json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Writer.Lines.Should().BeEmpty("棄却した要求は OpenD の標準入力へ 1 バイトも書いてはならない");
    }

    [Theory]
    // 画像 CAPTCHA を待っているときは 4 文字の英数だけが通る（数字 6 桁は通らない）。
    [InlineData("""{"code":"123456"}""")]
    [InlineData("""{"code":"ab1"}""")]
    [InlineData("""{"code":"ab-1"}""")]
    public async Task 画像待ちのときは画像の書式だけが通る(string json)
    {
        _factory.GivenConsole(WaitingForPic);

        var response = await PostAsync(json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Writer.Lines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task JSONとして読めない本文は400で何も書かない(string body)
    {
        var response = await PostAsync(body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Writer.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task 大きすぎる本文は413で何も書かない()
    {
        var padding = new string('9', 4096);
        var response = await PostAsync($$"""{"kind":"phone","code":"123456","pad":"{{padding}}"}""");

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _factory.Writer.Lines.Should().BeEmpty();
    }

    // ---- POST /opend-auth/verify: OpenD が居ない / 流量制限 ------------------------------

    [Fact]
    public async Task 読み手が居なければ503を返す()
    {
        _factory.GivenConsole(WaitingForPhone);
        _factory.Writer.Outcome = StdinWriteOutcome.NoReader;

        var response = await PostAsync("""{"code":"123456"}""");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("opend_not_listening");
    }

    [Fact]
    public async Task 書き込めない環境では503を返す()
    {
        _factory.GivenConsole(WaitingForPhone);
        _factory.Writer.Outcome = StdinWriteOutcome.Unavailable;

        var response = await PostAsync("""{"code":"123456"}""");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("stdin_unavailable");
    }

    [Fact]
    public async Task 窓あたりの上限を超えたら429で書かない()
    {
        _factory.GivenConsole(WaitingForPhone);

        for (var i = 0; i < 3; i++)
        {
            (await PostResendAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var blocked = await PostResendAsync();

        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Headers.RetryAfter.Should().NotBeNull("いつ再試行できるかを返す");
        _factory.Writer.Lines.Should().HaveCount(3, "上限を超えた 4 件目は SMS 枠を消費しない");

        _factory.Time.Advance(TimeSpan.FromSeconds(60));
        (await PostResendAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 壊れた投入は流量制限の枠を消費しない()
    {
        _factory.GivenConsole(WaitingForPhone);

        for (var i = 0; i < 20; i++)
        {
            (await PostAsync("""{"code":"bad"}""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await PostAsync("""{"code":"123456"}""")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- GET /opend-auth/state: 🔴 3 状態はサーバが宣言する ------------------------------

    [Theory]
    // 読めて入力待ち → waiting。読めて入力待ちでない → idle（**対象なし**）。
    // 読めない → unavailable（**供給が無い**）。後ろ 2 つを取り違えると
    // 「正常にログインできているように見える」（planning#594）。
    [InlineData(WaitingForPhone, "waiting", "phone")]
    [InlineData(WaitingForPic, "waiting", "pic")]
    [InlineData(NotWaiting, "idle", null)]
    public async Task 状態は3状態をサーバ側で宣言する(string console, string expectedStatus, string? expectedPrompt)
    {
        _factory.GivenConsole(console);

        var state = await _client.GetFromJsonAsync<JsonElement>("/opend-auth/state");

        state.GetProperty("status").GetString().Should().Be(expectedStatus);
        state.GetProperty("prompt").GetString().Should().Be(expectedPrompt);
        state.GetProperty("consoleAvailable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task 状態は取得できないことを対象なしと混ぜない()
    {
        // コンソールの複製を置かない。
        var state = await _client.GetFromJsonAsync<JsonElement>("/opend-auth/state");

        state.GetProperty("status").GetString().Should().Be("unavailable",
            "「読めていない」を「入力待ちでない（idle）」へ倒すと、壊れているのに正常に見える");
        state.GetProperty("consoleAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task 最後のログイン時刻は推測で埋めない()
    {
        _factory.GivenConsole(NotWaiting);

        var state = await _client.GetFromJsonAsync<JsonElement>("/opend-auth/state");

        state.GetProperty("lastLoginAt").ValueKind.Should().Be(JsonValueKind.Null,
            "OpenD のコンソールは成功の行に時刻を持たない。作れない値を埋めると「取得できていない」が化ける");
    }

    // ---- GET /opend-auth/state ---------------------------------------------------------

    [Fact]
    public async Task 状態はプロンプトと整形済みのコンソール末尾を返す()
    {
        _factory.GivenConsole("Login...\nCommand Tips: input_pic_verify_code -code=<code>\n");

        var state = await _client.GetFromJsonAsync<JsonElement>("/opend-auth/state");

        state.GetProperty("prompt").GetString().Should().Be("pic");
        state.GetProperty("consoleAvailable").GetBoolean().Should().BeTrue();
        state.GetProperty("consoleTail").GetString().Should().Contain("input_pic_verify_code");
    }

    [Fact]
    public async Task 状態はコンソールに残ったコードを返さない()
    {
        _factory.GivenConsole("input_phone_verify_code -code=135790\nCommand Tips: input_phone_verify_code\n");

        var body = await _client.GetStringAsync("/opend-auth/state");

        body.Should().NotContain("135790", "script の記録には運用者が打ったコードがそのまま残る");
        body.Should().Contain("-code=***");
    }

    [Fact]
    public async Task 状態はコンソール末尾を上限で切る()
    {
        // 既定 8 KiB。**週単位で常駐する OpenD のログ全量を返さない。**
        _factory.GivenConsole(new string('x', 40_000) + "\nCommand Tips: input_phone_verify_code\n");

        var state = await _client.GetFromJsonAsync<JsonElement>("/opend-auth/state");

        state.GetProperty("consoleTail").GetString()!.Length.Should().BeLessThanOrEqualTo(8 * 1024);
        state.GetProperty("prompt").GetString().Should().Be("phone", "末尾は残るのでプロンプトは読める");
    }

    [Fact]
    public async Task コンソールが無くても200を返す()
    {
        var path = Path.Combine(_factory.RuntimeDirectory, "console.log");
        if (File.Exists(path)) File.Delete(path);

        var state = await _client.GetFromJsonAsync<JsonElement>("/opend-auth/state");

        state.GetProperty("consoleAvailable").GetBoolean().Should().BeFalse();
        state.GetProperty("prompt").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- GET /opend-auth/captcha -------------------------------------------------------

    [Fact]
    public async Task 画像があればPNGとして返す()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        _factory.GivenCaptcha(png);

        var response = await _client.GetAsync("/opend-auth/captcha");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(png);
    }

    [Fact]
    public async Task 画像が無ければ404を返す()
    {
        var path = Path.Combine(_factory.RuntimeDirectory, "captcha.png");
        if (File.Exists(path)) File.Delete(path);

        (await _client.GetAsync("/opend-auth/captcha")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 🔴 パス引数を取らないこと。取れるようにすると、同居する PVC
    /// （<c>Device.dat</c> / <c>OpenD.xml</c>）の読み出しへ一歩で繋がる。
    /// </summary>
    [Theory]
    [InlineData("/opend-auth/captcha/../../etc/passwd")]
    [InlineData("/opend-auth/captcha/opend.xml")]
    [InlineData("/opend-auth/captcha?path=/opt/opend/OpenD.xml")]
    public async Task 画像の取得はパスを選べない(string url)
    {
        var response = await _client.GetAsync(url);

        // クエリを付けても無視して固定パスを見るだけ（404 か、写しが在れば PNG）。
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        }
    }

    // ---- 公開している口はちょうど 3 本である -------------------------------------------

    [Theory]
    [InlineData("/opend-auth/console")]
    [InlineData("/opend-auth/command")]
    [InlineData("/opend-auth/exec")]
    [InlineData("/opend-auth")]
    public async Task 公開している3本以外の口は無い(string url)
        => (await _client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task 検証の投入はPOSTだけである()
    {
        (await _client.GetAsync("/opend-auth/verify")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}
