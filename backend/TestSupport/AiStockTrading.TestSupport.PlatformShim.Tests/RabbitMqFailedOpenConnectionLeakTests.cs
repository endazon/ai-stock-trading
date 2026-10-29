using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, #1482, IADR-0595: **開き損ねた接続が、裏で自動回復を続けて後から生き返らない**こと。
//
// 稼働 PoC（2026-10-26）: クラスタの起動直後にブローカの準備が整う前に各 Worker が起動し、Wolverine はブローカの開始を
// 再試行した（Attempt N of 20）。準備が整った後、AST の接続は 121 本（平常 22 本）に増え、余分はチャネル 0 のまま生きていた
// （connected_at はどれも同じ数十ミリ秒の間）。
//
// 根本原因（RabbitMQ.Client 7.1.2 を逆コンパイルして確認）: AutorecoveringConnection.CreateAsync は回復の購読をした後に
// 内側の接続を開き、開くのに失敗しても（ハンドシェイクの途中で切られた・応答が無い）その AutorecoveringConnection を閉じない。
// 開き損ねの shutdown（Library・541 "FailedOpen"）が自動回復を起動し、例外を受けた呼び出し元（Wolverine の起動の再試行）が
// 次の試行で新しい接続を作る裏で、**捨てられた接続が回復の間隔ごとに繋ぎ直しを続け、ブローカの準備が整った時点で一斉に繋がる**。
// Wolverine は接続の自動回復を必ず有効にする（RabbitMqTransport.configureDefaults）。7.2.x は開き損ねた接続を閉じて破棄する。
//
// 試験: AMQP のプロトコルヘッダを受けたら黙って切る偽のブローカへ、Wolverine と同じく自動回復を有効にして接続する。
// 開くのは失敗する（期待どおり）。その後、回復の間隔の何倍待っても、偽のブローカへの接続の試みは最初の 1 回だけである。
public class RabbitMqFailedOpenConnectionLeakTests
{
    [Fact]
    public async Task 開き損ねた接続は裏で繋ぎ直しを続けない()
    {
        using var broker = new HandshakeDroppingBroker();
        var factory = new ConnectionFactory
        {
            Uri = new Uri($"amqp://guest:guest@127.0.0.1:{broker.Port}"),
            // Wolverine の RabbitMqTransport.configureDefaults と同じ（接続の自動回復は常に有効）。
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromMilliseconds(100),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(5),
        };

        var open = async () => await factory.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await open.Should().ThrowAsync<BrokerUnreachableException>("前提: 偽のブローカはハンドシェイクの途中で切る");
        broker.Accepted.Should().Be(1, "前提: 開く試みは 1 回");

        // 回復の間隔（100 ms）の 15 倍待つ。是正前（7.1.2）はこの間に捨てられた接続が繋ぎ直しを続ける。
        await Task.Delay(TimeSpan.FromMilliseconds(1_500), TestContext.Current.CancellationToken);

        broker.Accepted.Should().Be(1, "開き損ねた接続は閉じて破棄され、裏で自動回復を続けない（#1482）");
    }

    // AMQP のプロトコルヘッダ（8 バイト）を読んだら何も返さずに切る TCP の受け口。受けた接続の数を数える。
    private sealed class HandshakeDroppingBroker : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;

        public HandshakeDroppingBroker()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _accepted);
                using (client)
                {
                    var header = new byte[8];
                    try
                    {
                        var stream = client.GetStream();
                        var read = 0;
                        while (read < header.Length)
                        {
                            var n = await stream.ReadAsync(header.AsMemory(read), _stop.Token).ConfigureAwait(false);
                            if (n == 0)
                                break;
                            read += n;
                        }
                    }
                    catch (Exception)
                    {
                        // 読めなくても切るだけ。
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
