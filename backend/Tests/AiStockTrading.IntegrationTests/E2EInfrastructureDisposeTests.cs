using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// NFR, #1128: 後片付けの破棄は「接続を閉じる待ちの打ち切り」だけを握り、ほかの例外は投げる。
// Docker も実基盤も要らない（Integration の印を付けない＝既定の CI で走る）。
public sealed class E2EInfrastructureDisposeTests
{
    private sealed class Throwing(Exception ex) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(ex);
    }

    private sealed class Recording : IAsyncDisposable
    {
        public int Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task 破棄の打ち切りは例外にしない()
    {
        // 2026-09-30 の実測: RabbitMQ の閉じ待ちが TaskCanceledException で打ち切られた。
        var act = () => E2EInfrastructure.DisposeQuietlyAsync(new Throwing(new TaskCanceledException()), "ホスト");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task 素のOperationCanceledExceptionも例外にしない()
    {
        // ホストの停止の打ち切りは TaskCanceledException ではなく OperationCanceledException で来る。
        var act = () => E2EInfrastructure.DisposeQuietlyAsync(new Throwing(new OperationCanceledException()), "ホスト");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task 打ち切り以外の例外はそのまま投げる()
    {
        var act = () => E2EInfrastructure.DisposeQuietlyAsync(new Throwing(new InvalidOperationException("壊れた")), "ホスト");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("壊れた");
    }

    [Fact]
    public async Task 破棄は1回だけ呼びnullは何もしない()
    {
        var host = new Recording();

        await E2EInfrastructure.DisposeQuietlyAsync(host, "ホスト");
        await E2EInfrastructure.DisposeQuietlyAsync(null, "無いホスト");

        host.Disposed.Should().Be(1);
    }
}
