using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AiStockTrading.Shared.Infrastructure.Composable.Http;
using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.Shared.Infrastructure.Tests;

// NFR, NFR-18, ADR-0006, #1535, IADR-0615 決定5: LLM ゲートウェイの https は、基盤の CA（ConfigMap platform-infra-ca の ca.crt）で検証する。
// ループバックの TLS の受け手（試験の CA が発行した証明書）へ、本番と同じハンドラ（PlatformInfraCaTrust.CreateHandler）で繋ぐ。
// 🔴 名前の違う証明書・CA が無い・別の CA のときは失敗し、平文や検証なしへ倒れない。
public sealed class PlatformInfraCaTrustTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("infra-ca-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static X509Certificate2 NewCa(string name)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 NewLeaf(X509Certificate2 ca, string dnsName)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(8);
        using var cert = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
        // SslStream の受け手は秘密鍵つきの証明書を要る（PFX へ一度通すと Linux でも鍵が結び付く）。
        return X509CertificateLoader.LoadPkcs12(cert.CopyWithPrivateKey(key).Export(X509ContentType.Pfx), null);
    }

    private string WritePem(X509Certificate2 cert, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, cert.ExportCertificatePem());
        return path;
    }

    // 1 回だけ TLS を受けて HTTP の 200 を返す受け手。
    private static (int Port, Task Served) Serve(X509Certificate2 leaf)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = leaf,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
                var buffer = new byte[4096];
                _ = await ssl.ReadAsync(buffer);
                var body = "ok";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                // 受け手側の失敗（クライアントが検証で切った）は試験の期待の側で見る。
            }
            finally
            {
                listener.Stop();
            }
        });
        return (port, served);
    }

    private static async Task<HttpResponseMessage> GetAsync(string? caPath, int port)
    {
        using var http = new HttpClient(PlatformInfraCaTrust.CreateHandler(caPath)) { Timeout = TimeSpan.FromSeconds(20) };
        return await http.GetAsync(new Uri($"https://localhost:{port}/health"));
    }

    [Fact]
    public async Task 基盤のCAが発行した証明書は検証を通る()
    {
        using var ca = NewCa("platform-infra-test-ca");
        using var leaf = NewLeaf(ca, "localhost");
        var (port, served) = Serve(leaf);

        using var response = await GetAsync(WritePem(ca, "ca.crt"), port);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
        await served;
    }

    [Fact]
    public async Task 名前の違う証明書は同じCAでも失敗する()
    {
        using var ca = NewCa("platform-infra-test-ca");
        using var leaf = NewLeaf(ca, "other.example");
        var (port, served) = Serve(leaf);

        var act = () => GetAsync(WritePem(ca, "ca.crt"), port);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithInnerException<AuthenticationException>();
        await served;
    }

    [Fact]
    public async Task CAが無いときは失敗する()
    {
        using var ca = NewCa("platform-infra-test-ca");
        using var leaf = NewLeaf(ca, "localhost");

        var (port1, served1) = Serve(leaf);
        var notConfigured = () => GetAsync(caPath: null, port1);
        (await notConfigured.Should().ThrowAsync<HttpRequestException>()).WithInnerException<AuthenticationException>();
        await served1;

        // 設定はあるがファイルが無い（ConfigMap のマウント漏れ）も同じく失敗する。
        var (port2, served2) = Serve(leaf);
        var missingFile = () => GetAsync(Path.Combine(_dir, "missing.crt"), port2);
        (await missingFile.Should().ThrowAsync<HttpRequestException>()).WithInnerException<AuthenticationException>();
        await served2;
    }

    [Fact]
    public async Task 別のCAが発行した証明書は失敗する()
    {
        using var ca = NewCa("platform-infra-test-ca");
        using var other = NewCa("other-test-ca");
        using var leaf = NewLeaf(other, "localhost");
        var (port, served) = Serve(leaf);

        var act = () => GetAsync(WritePem(ca, "ca.crt"), port);

        (await act.Should().ThrowAsync<HttpRequestException>()).WithInnerException<AuthenticationException>();
        await served;
    }

    // 外部 API（公開の CA）の証明書は、システムの信頼ストアの検証で誤りが無ければそのまま受ける（専用の検証が狭めない）。
    // 名前の不一致・証明書なしは、基盤の CA があっても受けない。
    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, false)]
    public void システムの検証の結果を狭めず名前の不一致は常に拒む(SslPolicyErrors errors, bool expected)
    {
        using var ca = NewCa("platform-infra-test-ca");
        using var leaf = NewLeaf(ca, "localhost");

        PlatformInfraCaTrust.Validate(ca, leaf, errors).Should().Be(expected);
    }
}
