using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpendAuthGateway.Common;
using OpendAuthGateway.Infrastructure;
using OpendAuthGateway.Tests.Infrastructure;

namespace OpendAuthGateway.Tests;

/// <summary>
/// #722: サイドカーの HTTP 面を、実 FIFO・実 OpenD 無しで動かす。
/// <para>
/// 共有 <c>emptyDir</c> の代わりに一時ディレクトリを指し、標準入力への書き込み口を
/// <see cref="RecordingOpendStdinWriter"/> へ差し替える（＝何が書かれたかを数える）。
/// </para>
/// </summary>
public sealed class OpendAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>OpenD コンテナと共有する <c>emptyDir</c> に相当する一時ディレクトリ。</summary>
    public string RuntimeDirectory { get; } =
        Directory.CreateTempSubdirectory("opend-auth-tests-").FullName;

    /// <summary>差し替えた書き込み口。試験はここを見て「書かれたか / 書かれなかったか」を判定する。</summary>
    public RecordingOpendStdinWriter Writer { get; } = new();

    /// <summary>差し替えた時刻源（流量制限の窓を進めるため）。</summary>
    public FakeTimeProvider Time { get; } = new();

    /// <summary>
    /// IADR-0566: 呼び出し元の検証に使う共有シークレット（試験用の値。<see cref="CreateClient()"/> の既定ヘッダーに載る）。
    /// </summary>
    public const string CallerSecret = "test-caller-secret-0123456789abcdef-0123456789";

    /// <summary>共有シークレットのファイル（Secret の volume のマウント先に相当）。</summary>
    /// <remarks>共有領域（emptyDir）の<b>外</b>に置く —— 本番でも別の volume であり、OpenD 本体からは見えない。</remarks>
    public string CallerSecretFile => RuntimeDirectory + ".caller-secret";

    /// <summary>共有シークレットのファイルの中身を差し替える（null は削除＝未構成）。</summary>
    public void GivenCallerSecret(string? content)
    {
        if (content is null) File.Delete(CallerSecretFile);
        else File.WriteAllText(CallerSecretFile, content);
    }

    /// <summary>ヘッダーを載せない素のクライアント（呼び出し元の検証の否定形に使う）。</summary>
    public HttpClient CreateClientWithoutCallerSecret()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Remove(OpendAuthGateway.Features.OpendAuth.CallerSecretFilter.HeaderName);
        return client;
    }

    /// <inheritdoc />
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(OpendAuthGateway.Features.OpendAuth.CallerSecretFilter.HeaderName, CallerSecret);
    }

    /// <summary>コンソール複製（<c>console.log</c>）を置く。</summary>
    public void GivenConsole(string content) =>
        File.WriteAllText(Path.Combine(RuntimeDirectory, OpendAuthOptions.ConsoleLogFileName), content);

    /// <summary>画像 CAPTCHA の写し（<c>captcha.png</c>）を置く。</summary>
    public void GivenCaptcha(byte[] content) =>
        File.WriteAllBytes(Path.Combine(RuntimeDirectory, OpendAuthOptions.CaptchaFileName), content);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        GivenCallerSecret(CallerSecret);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OpendAuthOptions.SectionName}:RuntimeDirectory"] = RuntimeDirectory,
            [$"{OpendAuthOptions.SectionName}:RateLimitMaxSubmissions"] = "3",
            [$"{OpendAuthOptions.SectionName}:RateLimitWindowSeconds"] = "60",
            [$"{OpendAuthOptions.SectionName}:MaxRequestBodyBytes"] = "512",
            [$"{OpendAuthOptions.SectionName}:CallerSecretFile"] = CallerSecretFile,
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOpendStdinWriter>();
            services.AddSingleton<IOpendStdinWriter>(Writer);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            File.Delete(CallerSecretFile);
            Directory.Delete(RuntimeDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗で試験を落とさない。
        }
    }
}
