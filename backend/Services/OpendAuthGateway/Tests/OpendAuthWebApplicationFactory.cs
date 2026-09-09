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

    /// <summary>コンソール複製（<c>console.log</c>）を置く。</summary>
    public void GivenConsole(string content) =>
        File.WriteAllText(Path.Combine(RuntimeDirectory, OpendAuthOptions.ConsoleLogFileName), content);

    /// <summary>画像 CAPTCHA の写し（<c>captcha.png</c>）を置く。</summary>
    public void GivenCaptcha(byte[] content) =>
        File.WriteAllBytes(Path.Combine(RuntimeDirectory, OpendAuthOptions.CaptchaFileName), content);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{OpendAuthOptions.SectionName}:RuntimeDirectory"] = RuntimeDirectory,
            [$"{OpendAuthOptions.SectionName}:RateLimitMaxSubmissions"] = "3",
            [$"{OpendAuthOptions.SectionName}:RateLimitWindowSeconds"] = "60",
            [$"{OpendAuthOptions.SectionName}:MaxRequestBodyBytes"] = "512",
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
            Directory.Delete(RuntimeDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗で試験を落とさない。
        }
    }
}
