using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// 🔴 NFR, IADR-0591 決定 1, MSP#2265: Wolverine の停止を 1 本の作業に束ねる gate。作業仕様書の規則 11 の表の P1〜P4 を 1 対 1 で写す。
//
// 停止の本体（Wolverine の runtime の StopAsync）を、完了を外から制御できる偽物にして、窓（本体の排出の最中）を決定的に開く。
// 変異: gate を「前の端だけ」（2 本目以降は旗だけを見て、すぐ戻る＝Wolverine の現状）へ戻すと P1・P4 が赤になる。
public sealed class WolverineSingleStopGateTests
{
    private sealed class ControlledStop
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _done.Task;
        }

        public void Complete() => _done.SetResult();

        public void Fail(Exception ex) => _done.SetException(ex);
    }

    [Fact]
    public async Task P1_並んだ2本の停止はどちらも本体の完了より前に終わらず本体は1回だけ走る()
    {
        var stop = new ControlledStop();
        var gate = new WolverineSingleStopGate(stop.StopAsync);

        // ① WebApplicationFactory.DisposeAsync 由来と ② RunAsync の WaitForShutdownAsync 由来の 2 本。
        var first = gate.StopAsync(CancellationToken.None);
        var second = gate.StopAsync(CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        first.IsCompleted.Should().BeFalse("本体（排出）の最中");
        second.IsCompleted.Should().BeFalse("2 本目が先に終わると、RunAsync が排出の途中でコンテナを破棄する（MSP#2265）");

        stop.Complete();
        await Task.WhenAll(first, second);
        stop.Calls.Should().Be(1, "排出は 1 回だけ走る");
    }

    [Fact]
    public async Task P2_停止が1本だけなら本体の完了で終わる()
    {
        var stop = new ControlledStop();
        var gate = new WolverineSingleStopGate(stop.StopAsync);

        var only = gate.StopAsync(CancellationToken.None);
        only.IsCompleted.Should().BeFalse();

        stop.Complete();
        await only;
        stop.Calls.Should().Be(1, "本番（SIGTERM で RunAsync だけが止める）では従来どおり 1 回");
    }

    [Fact]
    public async Task P3_停止の後に遅れて来た停止はすぐ終わり本体は二度走らない()
    {
        var stop = new ControlledStop();
        var gate = new WolverineSingleStopGate(stop.StopAsync);
        var first = gate.StopAsync(CancellationToken.None);
        stop.Complete();
        await first;

        var late = gate.StopAsync(CancellationToken.None);

        late.IsCompletedSuccessfully.Should().BeTrue();
        stop.Calls.Should().Be(1);
    }

    [Fact]
    public async Task P4_本体が例外で終われば全ての呼び出し元に同じ例外が届く()
    {
        var stop = new ControlledStop();
        var gate = new WolverineSingleStopGate(stop.StopAsync);
        var first = gate.StopAsync(CancellationToken.None);
        var second = gate.StopAsync(CancellationToken.None);

        stop.Fail(new InvalidOperationException("排出に失敗"));

        await ((Func<Task>)(() => first)).Should().ThrowAsync<InvalidOperationException>().WithMessage("排出に失敗");
        await ((Func<Task>)(() => second)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("排出に失敗", "2 本目にも失敗を届ける（握りつぶさない・止まらない）");
    }

    [Fact]
    public async Task P4_本体が同期的に投げても作業として届き呼び出しは止まらない()
    {
        var gate = new WolverineSingleStopGate(_ => throw new InvalidOperationException("同期の失敗"));

        var first = gate.StopAsync(CancellationToken.None);
        var second = gate.StopAsync(CancellationToken.None);

        await ((Func<Task>)(() => first)).Should().ThrowAsync<InvalidOperationException>();
        await ((Func<Task>)(() => second)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task 共通配線はgateをWolverineのruntimeより後に登録し_停止ではruntimeより先に呼ばれる()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.UseAiStockTradingRabbitMq("ai-stock-trading.stop-gate-test", "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var hosted = host.Services.GetServices<IHostedService>().ToList();
        var runtimeIndex = hosted.FindIndex(s => s is IWolverineRuntime);
        var gateIndex = hosted.FindIndex(s => s is WolverineSingleStopGate);

        runtimeIndex.Should().BeGreaterThanOrEqualTo(0, "Wolverine の runtime が hosted service に居る");
        gateIndex.Should().BeGreaterThan(runtimeIndex, "停止は登録の逆順。gate が後に並べば、停止では runtime より先に呼ばれる");
        hosted.Count(s => s is WolverineSingleStopGate).Should().Be(1);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task 実ホストで停止を2本並べて呼んでも両方が終わる()
    {
        // 窓の再現そのものは RabbitMQ の接続が要るが、束ねた停止が実ホストの二重の停止で詰まらない（自分を待たない）ことはここで確かめる。
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PinDynamicTypeLoadMode();
                opts.UseAiStockTradingRabbitMq("ai-stock-trading.stop-gate-test", "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var both = Task.WhenAll(host.StopAsync(CancellationToken.None), host.StopAsync(CancellationToken.None));

        (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)))
            .Should().BeSameAs(both, "二重の停止がどちらも終わる");
        await both;
    }
}
