using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace OrderExecutionService.Tests;

// #141, FR-05, IADR-0074: 自動リコンサイルの定期実行。既定無効・終端化した予約の OrderExecuted 発行・
// fail-safe（不確定は発行しない）を Wolverine のテストハーネス（Wolverine.Tracking）で検証する
// （ADR-0013 / IADR-0129 / #354。harness.Published → session.Sent。表明の意味は同じ）。
public class OrderReservationReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StalledAt = Now.AddHours(-48);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubProbe(ReservationProbeResult result) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private static BrokerOrder Placed(string orderId) =>
        new(orderId,
            new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 100m),
            OrderStatus.Filled, FilledQuantity: 10, AveragePrice: 100m, PlacedAt: StalledAt, CompletedAt: StalledAt);

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildHostAsync(
        IReservationBrokerProbe probe, InMemoryOrderReservationStore reservations,
        ReconciliationOptions? options = null) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // #856, IADR-0362: リコンサイラは解放の門（ReleaseOnNotPlaced）を構成から読む。
                // 本番と同じく DI から渡す（未登録なら既定＝門は閉じている）。
                opts.Services.AddSingleton(Options.Create(options ?? new ReconciliationOptions { Enabled = true }));
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton<IOrderReservationStore>(reservations);
                opts.Services.AddSingleton<IExecutedOrderStore, InMemoryExecutedOrderStore>();
                opts.Services.AddSingleton(probe);
                // FR-20, #386, IADR-0149 決定1: リコンサイラが再発行する OrderExecuted には
                // 実際に発注したアダプタの発注先が載る。本テストの構成は paper（実ブローカへ接続しない）。
                opts.Services.AddSingleton<IBrokerAdapter>(new PaperBrokerAdapter());
                opts.Services.AddScoped<OrderReservationReconciler>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderReservationReconciliationService BuildService(
        IHost host, ReconciliationOptions options) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            // 常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
            host.Services.GetRequiredService<IClock>(),
            Options.Create(options),
            NullLogger<OrderReservationReconciliationService>.Instance);

    [Fact]
    public async Task 発注済み確定でOrderExecutedが発行される()
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(new StubProbe(ReservationProbeResult.Placed(Placed("BRK-7"))), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        ReservationReconciliationResult result = null!;
        Func<IMessageContext, Task> reconcile = async _ =>
            result = await service.ReconcileOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);

        result.Terminalized.Should().Be(1);
        // 🔴 T-10-604, #856: 突合で確定した建玉には保護レグが張られない（#853 の 2 番）。結果に載せて可視にする。
        result.ProbeTerminalized.Should().ContainSingle().Which.DecisionId.Should().Be(decisionId);
        session.Sent.MessagesOf<OrderExecuted>().Should().Contain(m => m.DecisionId == decisionId);
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Completed);

        await host.StopAsync();
    }

    [Fact]
    public async Task 不確定では何も発行されず据え置かれる()
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(new IndeterminateReservationBrokerProbe(), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        ReservationReconciliationResult result = null!;
        Func<IMessageContext, Task> reconcile = async _ =>
            result = await service.ReconcileOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);

        result.Indeterminate.Should().Be(1);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);

        await host.StopAsync();
    }

    [Fact]
    public async Task 無効時はExecuteAsyncが走査せず即座に戻る()
    {
        // fail-safe 既定: Enabled=false では滞留があっても一切触れない。
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(new StubProbe(ReservationProbeResult.NotPlaced), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = false });

        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(_ => StartAndStopAsync(service));

        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved, "無効時は解放しない");
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 常駐経由でも門が閉じた未発注判定は解放されない()
    {
        // 🔴 T-10-606（否定形）: 本番の合成（常駐 → scope → リコンサイラ）を通しても、解放の門が閉じているあいだは
        // 在庫の押さえを解かない。**単体では閉じているのに配線で開く**という事故を塞ぐ（#848 の B2〜B4 と同じ型）。
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var host = await BuildHostAsync(
            new StubProbe(ReservationProbeResult.NotPlaced), reservations,
            new ReconciliationOptions { Enabled = true }); // ReleaseOnNotPlaced は既定 false
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        ReservationReconciliationResult result = null!;
        Func<IMessageContext, Task> reconcile = async _ =>
            result = await service.ReconcileOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);

        result.Released.Should().Be(0);
        result.HeldNotPlaced.Should().ContainSingle().Which.Should().Be(decisionId);
        reservations.Find(decisionId)!.State.Should().Be(OrderDispatchState.Reserved);
        session.Sent.MessagesOf<OrderExecuted>().Should().BeEmpty();

        await host.StopAsync();
    }

    [Fact]
    public async Task 発行が落ちても保護レグ不在のCriticalは出る()
    {
        // 🔴 T-10-609（否定形・#882 監査 N1）: 記録は**発行より先**に出す。
        //
        // 発行（PublishAsync）はメッセージ基盤に触れるため落ち得る。落ちた後ろに記録を置くと、例外で
        // ReconcileOnceAsync ごと抜けて記録が出ない。**しかも予約は既に MarkCompleted を commit 済みで、
        // 次回巡回の FindStalledReserved に載らない** —— 「保護レグの無い建玉が載った」Critical は二度と出なくなる。
        // 本 PR の中心的主張（黙って通り過ぎさせない）が、発行の成否に依ってはならない。
        //
        // 発行だけを確実に落とすため、**破棄済みの** Wolverine ランタイムで publish させる
        //（ObjectDisposedException）。リコンサイル本体は生きているホストの scope で動かす
        //（同じホストを破棄すると DI ごと死んで本体まで走らなくなり、見たい分岐に届かない）。
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        using var live = await BuildHostAsync(
            new StubProbe(ReservationProbeResult.Placed(Placed("BRK-DOWN"))), reservations);

        var brokenBus = await BuildHostAsync(
            new IndeterminateReservationBrokerProbe(), new InMemoryOrderReservationStore());
        var deadRuntime = brokenBus.Services.GetRequiredService<IWolverineRuntime>();
        await brokenBus.StopAsync();
        brokenBus.Dispose(); // 以降 deadRuntime 経由の PublishAsync は ObjectDisposedException を投げる

        var logger = new RecordingLogger();
        var service = new OrderReservationReconciliationService(
            live.Services.GetRequiredService<IServiceScopeFactory>(),
            deadRuntime,
            live.Services.GetRequiredService<IClock>(),
            Options.Create(new ReconciliationOptions { Enabled = true }),
            logger);

        var publish = async () => await service.ReconcileOnceAsync(CancellationToken.None);

        // 例外の型は Wolverine の内部事情（破棄済み端点の扱い）で変わり得るので固定しない。
        // 本テストが固定するのは「発行が落ちること」ではなく「落ちても記録は出ていること」である。
        await publish.Should().ThrowAsync<Exception>("発行の失敗は握り潰さない（常駐が拾って次巡回で再試行する）");
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Critical && e.Message.Contains("保護逆指値を張りません", StringComparison.Ordinal),
            "発行が落ちても、保護レグ不在の Critical は既に出ていなければならない");

        // ［2026-09-23 追記 / #890・IADR-0371］**巡回サマリは出ない**（是正前は出ていた）。
        // 記録と発行を 1 件ごと（確定の直後）へ移した結果、発行が落ちるとその位置で巡回が終わるため、
        // 巡回全体の件数を言える地点に到達しない。**これは意図した取り引きである** ——
        // サマリは「巡回が回りきって初めて言えること」であり、回りきっていない巡回の件数は部分値にすぎない。
        // 永久に失われる側（確定済み 1 件の Critical）は上で出ていることが本テストの主張である。
        logger.Entries.Should().NotContain(
            e => e.Message.Contains("滞留 1 件を走査", StringComparison.Ordinal),
            "巡回サマリは巡回が最後まで回ったときだけ出す（1 件ごとの明細と二重に出さないための位置でもある）");
        reservations.Find(decisionId)!.State.Should().Be(
            OrderDispatchState.Completed, "予約は確定済み＝次回巡回の走査対象に入らない（だから記録が最後の砦である）");
    }

    // ---- 🔴 #890, IADR-0371: 巡回の中断で、確定済みの所見と OrderExecuted を失わない ----

    // 照会のたびに任意の副作用を差し込めるプローブ（中断を決定的に起こすために使う。実時間の sleep は使わない）。
    private sealed class CancellingProbe(Action<Guid> onProbe, Func<Guid, ReservationProbeResult> fn)
        : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default)
        {
            onProbe(reservation.DecisionId);
            return Task.FromResult(fn(reservation.DecisionId));
        }
    }

    [Fact]
    public async Task 巡回が中断されても確定済みの保護レグ不在のCriticalは出る()
    {
        // 🔴 T-10-650（否定形・#890。#882 監査 PROBE5 と同じ形）: 本番の合成（常駐 → scope → リコンサイラ）で、
        // 2 件目の手前で停止要求が来た場合。**1 件目は既に MarkCompleted を commit 済みで次回巡回の
        // FindStalledReserved に載らない**ため、ここで出さなければその Critical は永久に失われる。
        // 是正前はループ先頭の ThrowIfCancellationRequested が記録より手前にあり、ログは 1 行も出なかった。
        var reservations = new InMemoryOrderReservationStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        reservations.TryReserve(first, StalledAt);
        reservations.TryReserve(second, StalledAt.AddSeconds(1));

        using var cts = new CancellationTokenSource();
        using var host = await BuildHostAsync(
            new CancellingProbe(_ => cts.Cancel(), _ => ReservationProbeResult.Placed(Placed("BRK-STOP"))),
            reservations);

        var logger = new RecordingLogger();
        var service = new OrderReservationReconciliationService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            host.Services.GetRequiredService<IClock>(),
            Options.Create(new ReconciliationOptions { Enabled = true }),
            logger);

        var reconcile = async () => await service.ReconcileOnceAsync(cts.Token);
        await reconcile.Should().ThrowAsync<OperationCanceledException>();

        logger.Entries.Where(
            e => e.Level == LogLevel.Critical && e.Message.Contains("保護逆指値を張りません", StringComparison.Ordinal))
            .Should().ContainSingle("確定済み 1 件の Critical が、ちょうど 1 行出ていなければならない")
            .Which.Message.Should().Contain(first.ToString());
        reservations.Find(first)!.State.Should().Be(
            OrderDispatchState.Completed, "確定済み＝次回巡回の走査対象に入らない（だからここが最後の砦である）");
        reservations.Find(second)!.State.Should().Be(OrderDispatchState.Reserved);

        await host.StopAsync();
    }

    [Fact]
    public async Task 巡回が中断されても確定済みのOrderExecutedは発行済みである()
    {
        // 🔴 T-10-651（否定形・#890）: 失われるのは所見だけではない。確定済み予約の `OrderExecuted` が出ないと、
        // **監査・リスク管理・通知は突合が確定させた約定を二度と受け取らない**（台帳に約定が載らない）。
        // 発行も確定の直後（1 件ごと）へ移したことを、本番と同じ Wolverine 配線で固定する。
        var reservations = new InMemoryOrderReservationStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        reservations.TryReserve(first, StalledAt);
        reservations.TryReserve(second, StalledAt.AddSeconds(1));

        using var cts = new CancellationTokenSource();
        using var host = await BuildHostAsync(
            new CancellingProbe(_ => cts.Cancel(), _ => ReservationProbeResult.Placed(Placed("BRK-STOP"))),
            reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        var cancelled = false;
        Func<IMessageContext, Task> reconcile = async _ =>
        {
            try
            {
                await service.ReconcileOnceAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        };
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);

        cancelled.Should().BeTrue("2 件目の手前で中断されること自体は変えていない");
        session.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle()
            .Which.DecisionId.Should().Be(first, "確定した 1 件目だけが発行され、据え置きの 2 件目は発行されない");

        await host.StopAsync();
    }

    [Fact]
    public async Task 中断された巡回の後続巡回でも同じ予約は二重発行されない()
    {
        // 🔴 T-10-652（否定形・#890）: 是正が二重発行の側へ倒れていないこと。中断で 1 件目を発行したあと
        // 次の巡回を回しても、1 件目は Completed で走査されないため発行は 1 通のままである。
        var reservations = new InMemoryOrderReservationStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        reservations.TryReserve(first, StalledAt);
        reservations.TryReserve(second, StalledAt.AddSeconds(1));

        using var cts = new CancellationTokenSource();
        using var host = await BuildHostAsync(
            new CancellingProbe(id => { if (id == first) cts.Cancel(); },
                _ => ReservationProbeResult.Placed(Placed("BRK-STOP"))),
            reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true });

        Func<IMessageContext, Task> reconcileTwice = async _ =>
        {
            try
            {
                await service.ReconcileOnceAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 中断された巡回。1 件目は確定済み・発行済みである。
            }

            await service.ReconcileOnceAsync(CancellationToken.None);
        };
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcileTwice);

        var sent = session.Sent.MessagesOf<OrderExecuted>().ToList();
        sent.Should().HaveCount(2);
        sent.Count(m => m.DecisionId == first).Should().Be(1, "確定済みの 1 件目は二重に発行されない");
        sent.Count(m => m.DecisionId == second).Should().Be(1, "据え置かれた 2 件目は次回巡回で発行される");

        await host.StopAsync();
    }

    // 記録の実物を見るための最小のスパイ（本リポジトリはモックライブラリを持たない）。
    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger<OrderReservationReconciliationService>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    // 常駐を起動して止めるだけの補助（元テストと呼び出し順は同じ）。
    private static async Task StartAndStopAsync(OrderReservationReconciliationService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
    }
}
