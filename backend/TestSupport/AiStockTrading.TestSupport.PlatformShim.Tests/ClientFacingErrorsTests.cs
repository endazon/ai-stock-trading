using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-06, IADR-0503, #1206: 群のフィルタ・gRPC の写しが応答へ載せる例外の文言を「自前のコードが投げたもの」に限る判定。
// 試験では本試験のアセンブリを「サービスのアセンブリ」として渡す（本試験のコードが投げた例外＝自前の送出）。
public class ClientFacingErrorsTests
{
    // 接続文字列に似た目印（資格情報は含めない）。固定文言の側に出たら漏れである。
    private const string ConnectionLikeMarker = "Host=db.internal;Port=5432;Database=app;Username=app_user";

    private static readonly System.Reflection.Assembly Own = typeof(ClientFacingErrorsTests).Assembly;

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

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }

    // T-10-2394: 自前のコードの送出（直接・CoreLib の送出用の補助関数経由・await の後）と共有アセンブリの送出は「自前」。
    [Fact]
    public async Task 自前のコードと共有アセンブリが投げた例外は自前と判定する()
    {
        ClientFacingErrors.IsRaisedByOwnCode(Catch(() => throw new ArgumentException("直接")), Own).Should().BeTrue();
        ClientFacingErrors.IsRaisedByOwnCode(Catch(() => ArgumentException.ThrowIfNullOrWhiteSpace(" ")), Own).Should().BeTrue(
            "CoreLib の ThrowIf 系は呼び出し元（自前）の検証として扱う");

        var asyncEx = await Assert.ThrowsAsync<ArgumentException>(ThrowsAfterAwaitAsync);
        ClientFacingErrors.IsRaisedByOwnCode(asyncEx, Own).Should().BeTrue();

        // AiStockTrading.Shared.* の送出（市場の通貨が未定義）。
        ClientFacingErrors.IsRaisedByOwnCode(Catch(() => MarketCurrency.Of((Market)999)), Own).Should().BeTrue();
    }

    // T-10-2395: フレームワーク（CoreLib 以外）の送出・投げられていない例外・別のサービスのアセンブリは「自前でない」（安全側）。
    [Fact]
    public void フレームワークの送出と投げられていない例外は自前と判定しない()
    {
        var regex = Catch(() => _ = new Regex(ConnectionLikeMarker + "("));
        regex.Should().BeAssignableTo<ArgumentException>();
        ClientFacingErrors.IsRaisedByOwnCode(regex, Own).Should().BeFalse();

        ClientFacingErrors.IsRaisedByOwnCode(new ArgumentException("投げていない"), Own).Should().BeFalse();

        // 呼び出し側のサービスと違うアセンブリ（ここでは PlatformShim 本体）を渡すと、本試験のコードの送出は自前でない。
        ClientFacingErrors.IsRaisedByOwnCode(Catch(() => throw new ArgumentException("直接")), typeof(ClientFacingErrors).Assembly)
            .Should().BeFalse();
    }

    // T-10-2396: 自前でない例外の文言は固定文言にし、元の例外を Warning で例外ごとログへ出す。自前の文言はそのまま返しログは出さない。
    [Fact]
    public void 自前でない例外は固定文言を返し元の例外をログへ出す()
    {
        var logger = new CapturingLogger();
        var framework = Catch(() => _ = new Regex(ConnectionLikeMarker + "("));

        var message = ClientFacingErrors.MessageFor(framework, Own, logger);

        message.Should().Be(ClientFacingErrors.InvalidRequestMessage).And.NotContain("db.internal");
        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Warning, framework));

        var own = Catch(() => throw new ArgumentException("market は必須です。"));
        ClientFacingErrors.MessageFor(own, Own, logger).Should().Be("market は必須です。");
        logger.Entries.Should().ContainSingle("自前の文言を返すときはログを足さない");

        ClientFacingErrors.MessageFor(framework, Own, logger, "別の固定文言").Should().Be("別の固定文言");
    }
}
