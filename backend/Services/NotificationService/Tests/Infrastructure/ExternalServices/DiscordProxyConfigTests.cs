using System.Net;
using System.Net.Sockets;
using System.Text;
using Discord.Net.Rest;
using Discord.WebSocket;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Xunit;

namespace NotificationService.Tests;

// NFR, IADR-0644 決定 2: Discord.Net の REST と Gateway（WebSocket）が、環境の中継の設定（HttpClient.DefaultProxy。
// HTTPS_PROXY・NO_PROXY から作られる）に従うこと。Discord.Net の既定の提供者はどちらも従わない（陰性対照で固定する）。
// 外へは出ない: 宛先は解決できない名前（.invalid）で、中継の役は手元の受け口（CONNECT を記録して 403 で閉じる）。
// HttpClient.DefaultProxy はプロセスに 1 つなので、並行しない集まりで走らせる。
[CollectionDefinition(nameof(DiscordProxyConfigCollection), DisableParallelization = true)]
public sealed class DiscordProxyConfigCollection;

[Collection(nameof(DiscordProxyConfigCollection))]
public sealed class DiscordProxyConfigTests
{
    private const string Host = "discord-proxy-probe.invalid";

    [Fact]
    public async Task 構成のRESTは環境の中継を通る()
    {
        var seen = await WithRecordingProxy(async () =>
        {
            var config = DiscordNetBotGateway.CreateSocketConfig();
            await SendRest(config.RestClientProvider);
        });
        seen.Should().ContainSingle().Which.Should().StartWith($"CONNECT {Host}:443");
    }

    [Fact]
    public async Task 構成のGatewayは環境の中継を通る()
    {
        var seen = await WithRecordingProxy(async () =>
        {
            var config = DiscordNetBotGateway.CreateSocketConfig();
            await ConnectGateway(config);
        });
        seen.Should().ContainSingle().Which.Should().StartWith($"CONNECT {Host}:443");
    }

    [Fact]
    public async Task 陰性対照_Discord_Netの既定の提供者は環境の中継を通らない()
    {
        var seen = await WithRecordingProxy(async () =>
        {
            var config = new DiscordSocketConfig();
            await SendRest(config.RestClientProvider);
            await ConnectGateway(config);
        });
        seen.Should().BeEmpty();
    }

    private static async Task SendRest(RestClientProvider provider)
    {
        var rest = provider($"https://{Host}/api/v10/");
        await Swallow(() => rest.SendAsync("GET", "gateway", CancellationToken.None));
    }

    private static async Task ConnectGateway(DiscordSocketConfig config)
    {
        var ws = config.WebSocketProvider();
        await Swallow(() => ws.ConnectAsync($"wss://{Host}/?v=10&encoding=json"));
    }

    private static async Task Swallow(Func<Task> f)
    {
        try
        {
            await f().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // 中継は 403 で閉じる・名前は解決できないので、どちらの経路でも失敗で終わる（記録だけを見る）。
        }
    }

    /// <summary>HttpClient.DefaultProxy を手元の受け口へ向けて <paramref name="act"/> を走らせ、受けた要求の最初の行を返す。</summary>
    private static async Task<IReadOnlyList<string>> WithRecordingProxy(Func<Task> act)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var buf = new byte[4096];
                    var n = await stream.ReadAsync(buf, stop.Token);
                    lock (seen)
                    {
                        seen.Add(Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0]);
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nconnection: close\r\n\r\n"), stop.Token);
                }
            }
        });

        var original = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{port}");
            await act();
        }
        finally
        {
            HttpClient.DefaultProxy = original;
            stop.Cancel();
            listener.Stop();
            try
            {
                await loop;
            }
            catch (Exception)
            {
                // 受け口を閉じた後の読み書きの失敗は無視する。
            }
        }

        lock (seen)
        {
            return seen.ToArray();
        }
    }
}
