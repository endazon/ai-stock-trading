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

    // T-10-2419: 印のある例外は文言をそのまま返す。印は型・文言・ParamName・ActualValue を変えない（ArgumentOutOfRangeException のまま）。
    // 印つきの ThrowIfNullOrWhiteSpace は CoreLib と同じ例外（null は ArgumentNullException）と同じ文言を投げる。
    // 印は ArgumentException の系統にだけ効く（同じキーを Data に置いた InvalidOperationException は印とみなさない）。
    [Fact]
    public void 印のある例外は文言を返し型と文言を変えない()
    {
        var logger = new CapturingLogger();

        var direct = Catch(() => throw new ArgumentException("market は必須です。", "req").ClientVisible());
        ClientFacingErrors.MessageFor(direct, logger).Should().Be(direct.Message).And.Contain("market は必須です。");

        var outOfRange = Catch(() => throw new ArgumentOutOfRangeException("count", 0, "件数は 1 以上").ClientVisible());
        var typed = outOfRange.Should().BeOfType<ArgumentOutOfRangeException>().Which;
        (typed.ParamName, typed.ActualValue).Should().Be(("count", (object?)0));
        ClientFacingErrors.MessageFor(outOfRange, logger).Should().Be(outOfRange.Message).And.Contain("件数は 1 以上");

        var expectedBlank = Catch(() => ArgumentException.ThrowIfNullOrWhiteSpace(" ", "reason"));
        var blank = Catch(() => ClientVisibleArgument.ThrowIfNullOrWhiteSpace(" ", "reason"));
        blank.Should().BeOfType<ArgumentException>();
        blank.Message.Should().Be(expectedBlank.Message);
        ClientFacingErrors.MessageFor(blank, logger).Should().Contain("'reason'");

        var expectedNull = Catch(() => ArgumentException.ThrowIfNullOrWhiteSpace(null, "reason"));
        var missing = Catch(() => ClientVisibleArgument.ThrowIfNullOrWhiteSpace(null, "reason"));
        missing.Should().BeOfType<ArgumentNullException>();
        missing.Message.Should().Be(expectedNull.Message);
        ClientFacingErrors.MessageFor(missing, logger).Should().Be(missing.Message);

        string? reason = "理由";
        ((Action)(() => ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason))).Should().NotThrow();

        logger.Entries.Should().BeEmpty("印のある文言を返すときはログを足さない");

        var notArgument = new InvalidOperationException(ConnectionLikeMarker);
        notArgument.Data[ClientVisibleArgument.DataKey] = true;
        notArgument.IsClientVisible().Should().BeFalse();
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
