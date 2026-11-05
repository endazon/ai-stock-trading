using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiStockTrading.Shared.Infrastructure.Composable.Http;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// NFR, NFR-18, #1535, IADR-0621 決定5: 基盤の CA が設定されているのに読めないとき、起動時に 1 回だけ Warning を出す
// （パスと理由だけ。ファイルの中身・例外の文言は出さない）。拒否の側へ倒れる挙動は変えない（PlatformInfraCaTrustTests が固定する）。
// 1 回の判定はプロセス内でパスごとなので、各試験は自分だけの一時パスを使う。
public sealed class PlatformInfraCaTrustLoadWarningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("infra-ca-warn-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ファイルが無いとパスと理由のWarningを1回だけ出す()
    {
        var path = Path.Combine(_dir, "missing.crt");
        var logger = new CapturingLogger();

        PlatformInfraCaTrust.CreateHandler(path, logger).Dispose();
        PlatformInfraCaTrust.CreateHandler(path, logger).Dispose();

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain(path).And.Contain("ファイルが無い");
    }

    [Fact]
    public void PEMとして読めないとパスと理由だけを出し中身は出さない()
    {
        const string content = "not-a-certificate-body-7f3a";
        var path = Path.Combine(_dir, "broken.crt");
        File.WriteAllText(path, content);
        var logger = new CapturingLogger();

        using var handler = PlatformInfraCaTrust.CreateHandler(path, logger);

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain(path).And.Contain("PEM").And.NotContain(content);
        PlatformInfraCaTrust.IsPlatformInfraCaHandler(handler).Should().BeTrue();
    }

    [Fact]
    public void 読めるCAと未設定ではWarningを出さない()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=warn-test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(_dir, "ca.crt");
        File.WriteAllText(path, ca.ExportCertificatePem());
        var logger = new CapturingLogger();

        PlatformInfraCaTrust.CreateHandler(path, logger).Dispose();
        PlatformInfraCaTrust.CreateHandler(caPath: null, logger).Dispose();
        PlatformInfraCaTrust.CreateHandler(" ", logger).Dispose();

        logger.Entries.Should().BeEmpty();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
    }
}
