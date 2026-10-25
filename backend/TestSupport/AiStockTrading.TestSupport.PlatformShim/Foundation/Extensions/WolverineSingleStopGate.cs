using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// 🔴 NFR, IADR-0129, IADR-0591 決定 1, MSP#2265: **Wolverine の停止を 1 本の作業に束ね、どの停止の呼び出し元もその完了を待つ。**
//
// in-process のホスト（WebApplicationFactory）では、停止が 2 回、並んで呼ばれる:
//   ① WebApplicationFactory.DisposeAsync → DeferredHost.StopAsync → Host.StopAsync
//   ② エントリポイントの app.RunAsync() → WaitForShutdownAsync（ApplicationStopping を受ける）→ Host.StopAsync
// そしてコンテナの破棄は ② の RunAsync の finally（Host.DisposeAsync）が行う。
//
// Wolverine 6 の WolverineRuntime.StopAsync は、冒頭で「停止した」旗を立ててから排出（DrainAsync）に入る。
// 2 回目の呼び出しは旗だけを見て**すぐに戻る**。そのため ② が先に終わり、RunAsync が排出の途中でコンテナを破棄する。
// コンテナの破棄は WolverineRuntime.DisposeAsync を通して RabbitMQ の接続を破棄する。その結果、排出の中でチャネルを
// 閉じていたリスナが、破棄されたセマフォを待ったまま**二度と起こされない**（手元の heap dump で実測。後片付けが永久に止まる）。
//
// 本 hosted service は Wolverine の runtime より**後に**登録する（共通配線 UseAiStockTradingRabbitMq の configure の中。
// UseWolverine は configure より前に runtime を hosted service 登録する）。停止は登録の逆順なので、各停止の呼び出しで
// Wolverine 自身の停止より**先に**本サービスが呼ばれ、最初の呼び出しが本体（runtime の StopAsync）を 1 回だけ走らせる。
// 2 本目以降は同じ作業を待つ。どの呼び出しも排出の完了より前に終わらないので、RunAsync の破棄は排出の後になる。
// Wolverine 自身の hosted service の停止は、その後に呼ばれて旗を見て戻る（二重には走らない）。
//
// 本番（1 プロセス 1 サービス・SIGTERM で RunAsync だけが止める）では呼び出しは 1 回で、振る舞いは従来と同じである。
internal sealed class WolverineSingleStopGate : IHostedService
{
    private readonly Func<CancellationToken, Task> _stop;
    private readonly Lock _lock = new();
    private Task? _stopping;

    internal WolverineSingleStopGate(Func<CancellationToken, Task> stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        _stop = stop;
    }

    internal static WolverineSingleStopGate For(IWolverineRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var hosted = runtime as IHostedService
            ?? throw new InvalidOperationException(
                $"Wolverine の runtime（{runtime.GetType().FullName}）が IHostedService ではない。停止を束ねられない（IADR-0591）。");
        return new WolverineSingleStopGate(hosted.StopAsync);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // 最初の呼び出しが本体を走らせ、以後の呼び出しは同じ作業（完了・例外とも）を返す。
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _stopping ??= StopOnceAsync(cancellationToken);
        }
    }

    // 本体が同期的に投げても、例外を作業（Task）に載せて全ての呼び出し元へ同じ形で届ける。
    private async Task StopOnceAsync(CancellationToken cancellationToken) =>
        await _stop(cancellationToken).ConfigureAwait(false);
}
