using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Errors;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-06, IADR-0503, IADR-0509, #1206, #1230: 群のフィルタ・gRPC の写しが応答へ載せる例外の文言は、明示の印（ClientVisibleArgument）の
// ある ArgumentException に限る。判定は例外の中身だけで決まり、送出元（自前・CoreLib・第三者）・スタック・JIT の段階・インライン化に依らない。
public class ClientFacingErrorsTests
{
    // 接続文字列に似た目印（資格情報は含めない）。固定文言の側に出たら漏れである。
    private const string ConnectionLikeMarker = "Host=db.internal;Port=5432;Database=app;Username=app_user";

    private static Exception Catch(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("例外が投げられなかった（前提の崩れ）。");
    }

    private static async Task ThrowsAfterAwaitAsync()
    {
        await Task.Yield();
        throw new ArgumentException("非同期の検証の文言");
    }

    // 第三者のライブラリの小さな補助の代役。呼び出し元へインライン化されるよう強制し、CoreLib のコレクションに投げさせる
    // （重複キーの文言はキーの値＝目印を引用する）。IADR-0503 のスタックの判定は、これが自前のフレームへ畳まれると「自前」と誤った。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ThirdPartyStyleAdd(Dictionary<string, int> map, string key) => map.Add(key, map.Count);

    private static Exception InlinedThirdPartyDuplicateKey() =>
        Catch(() =>
        {
            var map = new Dictionary<string, int>();
            ThirdPartyStyleAdd(map, ConnectionLikeMarker);
            ThirdPartyStyleAdd(map, ConnectionLikeMarker);
        });

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    // T-10-2418（否定形）: 印の無い ArgumentException は、送出元に依らず固定文言——第三者相当の補助（AggressiveInlining）・CoreLib
    // （正規表現）・自前のコードの直接の送出・自前のコードの ThrowIfNullOrWhiteSpace・await の後・投げていない例外のいずれも。
    // 補助は 1,000 回繰り返して呼ぶ（段階コンパイルが上がっても判定が変わらないこと。判定はスタックを読まない）。
    [Fact]
    public async Task 印の無い_ArgumentException_は送出元に依らず固定文言()
    {
        var logger = new CapturingLogger();
        for (var i = 0; i < 1_000; i++)
        {
            var inlined = InlinedThirdPartyDuplicateKey();
            inlined.Should().BeOfType<ArgumentException>();
            inlined.Message.Should().Contain("db.internal", "前提: 重複キーの文言はキーの値を引用する");
            ClientFacingErrors.MessageFor(inlined, logger).Should().Be(ClientFacingErrors.InvalidRequestMessage);
        }

        var unmarked = new List<Exception>
        {
            Catch(() => _ = new Regex(ConnectionLikeMarker + "(")),
            Catch(() => throw new ArgumentException("market は必須です。")),
            Catch(() => ArgumentException.ThrowIfNullOrWhiteSpace(" ", "reason")),
            Catch(() => ArgumentException.ThrowIfNullOrWhiteSpace(null, "reason")),
            await Assert.ThrowsAsync<ArgumentException>(ThrowsAfterAwaitAsync),
            new ArgumentException("投げていない"),
        };

        foreach (var exception in unmarked)
        {
            exception.IsClientVisible().Should().BeFalse();
            ClientFacingErrors.MessageFor(exception, logger).Should().Be(ClientFacingErrors.InvalidRequestMessage)
                .And.NotContain("db.internal");
        }
    }

    // T-10-2419: 印のある例外は文言を返す。印は型・文言・ParamName・ActualValue を変えない（ArgumentOutOfRangeException のまま）。
    // 印は ArgumentException の系統にだけ効く（同じキーを Data に置いた InvalidOperationException は印とみなさない）。
    // SC-01, SC-02, IADR-0586 決定 1, #1472: 応答の文言からは .NET が付ける末尾（` (Parameter 'x')`・`Actual value was N.` の行）を落とす。
    // 例外の Message そのものは変えない（ログは従来どおり引数名を持つ）。
    [Fact]
    public void 印のある例外は文言を返し型と文言を変えない()
    {
        var logger = new CapturingLogger();

        var direct = Catch(() => throw new ArgumentException("market（市場）は必須です。", "req").ClientVisible());
        direct.Message.Should().Contain("'req'", "例外の文言は変えない");
        ClientFacingErrors.MessageFor(direct, logger).Should().Be("market（市場）は必須です。");

        var outOfRange = Catch(() => throw new ArgumentOutOfRangeException("count", 0, "件数は 1 以上").ClientVisible());
        var typed = outOfRange.Should().BeOfType<ArgumentOutOfRangeException>().Which;
        (typed.ParamName, typed.ActualValue).Should().Be(("count", (object?)0));
        ClientFacingErrors.MessageFor(outOfRange, logger).Should().Be("件数は 1 以上");

        var outOfRangeNoValue = Catch(() => throw new ArgumentOutOfRangeException("count", "件数は 1 以上").ClientVisible());
        ClientFacingErrors.MessageFor(outOfRangeNoValue, logger).Should().Be("件数は 1 以上");

        var noParam = Catch(() => throw new ArgumentException("引数名の無い文言。").ClientVisible());
        ClientFacingErrors.MessageFor(noParam, logger).Should().Be("引数名の無い文言。");

        // 文言の途中に同じ形の文字列があっても、末尾でなければ落とさない。
        var embedded = Catch(() => throw new ArgumentException("前 (Parameter 'x') 後", "x").ClientVisible());
        ClientFacingErrors.MessageFor(embedded, logger).Should().Be("前 (Parameter 'x') 後");

        logger.Entries.Should().BeEmpty("印のある文言を返すときはログを足さない");

        var notArgument = new InvalidOperationException(ConnectionLikeMarker);
        notArgument.Data[ClientVisibleArgument.DataKey] = true;
        notArgument.IsClientVisible().Should().BeFalse();
    }

    // SC-01, SC-02, IADR-0586 決定 2, #1472: 印つきの空欄検査は CoreLib と同じ型・同じ ParamName の例外を、日本語の文言で投げる
    // （以前は英語の既定文言 `The value cannot be an empty string …` が画面と Discord に出た）。応答の文言は「理由は必須です。」だけになる。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void 印つきの空欄検査は日本語の文言で投げる(string? reason)
    {
        var logger = new CapturingLogger();
        var expected = Catch(() => ArgumentException.ThrowIfNullOrWhiteSpace(reason));

        var thrown = Catch(() => ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason, "理由"));

        thrown.GetType().Should().Be(expected.GetType(), "null は ArgumentNullException・空と空白は ArgumentException（CoreLib と同じ）");
        ((ArgumentException)thrown).ParamName.Should().Be("reason");
        thrown.IsClientVisible().Should().BeTrue();
        ClientFacingErrors.MessageFor(thrown, logger).Should().Be("理由は必須です。");
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void 印つきの空欄検査は値があれば投げない()
    {
        string? reason = "理由";
        ((Action)(() => ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason, "理由"))).Should().NotThrow();
    }

    // T-10-2396: 印の無い例外の文言は固定文言にし、元の例外を Warning で例外ごとログへ出す。印のある文言はそのまま返しログは出さない。
    // 固定文言は差し替えられる。
    [Fact]
    public void 印の無い例外は固定文言を返し元の例外をログへ出す()
    {
        var logger = new CapturingLogger();
        var framework = Catch(() => _ = new Regex(ConnectionLikeMarker + "("));

        var message = ClientFacingErrors.MessageFor(framework, logger);

        message.Should().Be(ClientFacingErrors.InvalidRequestMessage).And.NotContain("db.internal");
        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Warning, framework));

        var visible = Catch(() => throw new ArgumentException("market は必須です。").ClientVisible());
        ClientFacingErrors.MessageFor(visible, logger).Should().Be("market は必須です。");
        logger.Entries.Should().ContainSingle("印のある文言を返すときはログを足さない");

        ClientFacingErrors.MessageFor(framework, logger, "別の固定文言").Should().Be("別の固定文言");
    }
}
