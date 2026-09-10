using System.Net;
using System.Net.Sockets;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Grpc;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR, MSP:ADR-0029, MSP:ADR-0075, IADR-0284 決定 5（段 0）, IADR-0328, #584:
// **解決した構成が本当に bind へ届いているか**を実 Kestrel のソケットで確かめる。
//
// 🔴 本群が無いと、本命の欠陥（h2c を足した瞬間に HTTP/1.1 のポートが消える）は緑のまま通る ——
// `GrpcListenerExtensionsTests` は構成の読み取りしか見ておらず、Kestrel が
// 「`Listen*` を 1 つでも構成するとホスティング URL を捨てる」性質はここでしか観測できない。
// readiness プローブ（`/health/ready`・8080）が落ちれば Pod は上がらない。
public sealed class GrpcListenerBindingTests
{
    // 🔴 空きポートは **OS の動的（ephemeral）範囲の外**から選ぶ（基盤 MSP#1355 の CI で実測した flake）。
    // 動的範囲は送信側ソケットの割当にも使われるため、「0 で取って解放し、後で bind し直す」形は
    // 並列テストと衝突して「address already in use」で一斉に落ちる。`Grpc:Port` に 0 は使えない（0 は「立てない」）。
    private static int FreeTcpPort()
    {
        var random = new Random();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = random.Next(20000, 30000);
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                listener.Start();
                return candidate;
            }
            catch (SocketException)
            {
                // 使用中。次の候補へ。
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("20000〜29999 に空きポートが見つからなかった。");
    }

    private static async Task<WebApplication> StartAsync(string httpUrls, int? grpcPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [GrpcListenerExtensions.UrlsKey] = httpUrls,
            [GrpcListenerExtensions.PortKey] = grpcPort?.ToString(),
        });
        builder.AddAiStockTradingGrpcListener();
        var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static bool CanConnect(int port)
    {
        using var client = new TcpClient();
        try
        {
            // 到達不能なら即座に拒否される（同一ホストなので待たされない）。
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // 🔴 これが本命。h2c を有効にしても HTTP/1.1 側のポートは残る。
    [Fact]
    public async Task h2c_を有効にしても_HTTP_のポートは消えない()
    {
        var httpPort = FreeTcpPort();
        var grpcPort = FreeTcpPort();
        await using var app = await StartAsync($"http://127.0.0.1:{httpPort}", grpcPort);

        CanConnect(httpPort).Should().BeTrue(
            "Kestrel は Listen* を構成するとホスティング URL を捨てるため、HTTP 側を再宣言していること");
        CanConnect(grpcPort).Should().BeTrue("h2c 専用ポートが立っていること（陽性対照）");

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // 陰性対照: `Grpc:Port` 未設定なら h2c ポートは立たない（既存サービスは 1 バイトも変わらない）。
    [Fact]
    public async Task Grpc_Port_が無ければ_h2c_ポートは立たない()
    {
        var httpPort = FreeTcpPort();
        var unusedGrpcPort = FreeTcpPort();
        await using var app = await StartAsync($"http://127.0.0.1:{httpPort}", grpcPort: null);

        CanConnect(httpPort).Should().BeTrue("HTTP 側は従来どおり立っていること（陽性対照）");
        CanConnect(unusedGrpcPort).Should().BeFalse("gRPC を構成していないのに h2c ポートが開かないこと");

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // `AddGrpc()` はリスナの有無に関わらず常に呼ぶ ——
    // `MapGrpcService` は `AddGrpc` 無しだと起動時に落ちるため、リスナの有無とサービス登録の可否を切り離す。
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddGrpc_はリスナの有無に関わらず登録される(bool withListener)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [GrpcListenerExtensions.UrlsKey] = "http://127.0.0.1:0",
            [GrpcListenerExtensions.PortKey] = withListener ? FreeTcpPort().ToString() : null,
        });

        builder.AddAiStockTradingGrpcListener();

        // MapGrpcService が起動時に探すマーカー（`GrpcMarkerService`。名前空間は internal のため型名で見る）。
        builder.Services.Should().Contain(d => d.ServiceType.Name == "GrpcMarkerService");
    }

    // 陰性対照: メッシュ内の TLS はサイドカーが終端する。https のホスティング URL と h2c は併存させない
    //（黙って無視すると「TLS で待っているつもりの平文ポート」を作ってしまう）。
    [Fact]
    public async Task https_のホスティング_URL_とは併存させない()
    {
        var act = async () => await StartAsync("https://127.0.0.1:0", FreeTcpPort());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*http のみ*");
    }
}
