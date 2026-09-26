using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using AiStockTrading.TestSupport.Messaging;
using AiStockTrading.TestSupport.Metrics;
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
        ReconciliationOptions? options = null, IReconciledEntryProtection? protection = null) =>
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
                // #853, IADR-0428 決定4: 保護の口（任意）。未登録ならリコンサイラの省略可能な引数は null（口が無い構成）。
                if (protection is not null)
                    opts.Services.AddSingleton(protection);
                opts.Services.AddScoped<OrderReservationReconciler>();

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static OrderReservationReconciliationService BuildService(
        IHost host, ReconciliationOptions options, BusinessMetrics? metrics = null) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            // 常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
            host.Services.GetRequiredService<IClock>(),
            Options.Create(options),
            // #856, IADR-0441: 判定の計数。計数を表明しない試験は既定名の計器でよい（肯定形の表明は他人の測定値に壊されない）。
            metrics ?? new BusinessMetrics(),
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
        // 🔴 T-10-604, #856: 突合で確定した建玉は、確定の時点では保護レグを持たない。結果に載せて可視にする
        //（本番の組み立てでは続けて保護レグを張る。#853・IADR-0428 決定4）。
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
            new BusinessMetrics(),
            logger);

        var publish = async () => await service.ReconcileOnceAsync(CancellationToken.None);

        // 例外の型は Wolverine の内部事情（破棄済み端点の扱い）で変わり得るので固定しない。
        // 本テストが固定するのは「発行が落ちること」ではなく「落ちても記録は出ていること」である。
        await publish.Should().ThrowAsync<Exception>("発行の失敗は握り潰さない（常駐が拾って次巡回で再試行する）");
        // ［2026-09-25 / #853・IADR-0428 決定4］文面は「この経路は保護逆指値を張りません」から「この時点では保護レグがありません
        //（続けて張る）」へ変わった（突合の後に保護レグを張る裁定）。本テストが固定する「発行より先に Critical が出る」は変わらない。
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Critical && e.Message.Contains("この時点では保護レグがありません", StringComparison.Ordinal),
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
            new BusinessMetrics(),
            logger);

        var reconcile = async () => await service.ReconcileOnceAsync(cts.Token);
        await reconcile.Should().ThrowAsync<OperationCanceledException>();

        logger.Entries.Where(
            e => e.Level == LogLevel.Critical && e.Message.Contains("この時点では保護レグがありません", StringComparison.Ordinal))
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

    [Fact]
    public async Task 中断しない巡回では確定ごとのCriticalがちょうど1行ずつ出る()
    {
        // 🔴 T-10-653（否定形・#890。PR #914 監査 M3）: 是正が「1 件ごとの出口」へ移したあと、
        // 巡回の末尾に明細のループを**戻す**と二重に出る。中断を含む T-10-650 / T-10-652 は
        // 巡回が最後まで行かないためこれを捕まえられない。**完走する巡回**で 1 件ずつであることを固定する。
        var reservations = new InMemoryOrderReservationStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        reservations.TryReserve(first, StalledAt);
        reservations.TryReserve(second, StalledAt.AddSeconds(1));

        using var host = await BuildHostAsync(
            new StubProbe(ReservationProbeResult.Placed(Placed("BRK-STOP"))), reservations);

        var logger = new RecordingLogger();
        var service = new OrderReservationReconciliationService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            host.Services.GetRequiredService<IClock>(),
            Options.Create(new ReconciliationOptions { Enabled = true }),
            new BusinessMetrics(),
            logger);

        await service.ReconcileOnceAsync(CancellationToken.None);

        var criticals = logger.Entries
            .Where(e => e.Level == LogLevel.Critical
                && e.Message.Contains("この時点では保護レグがありません", StringComparison.Ordinal))
            .ToList();
        criticals.Should().HaveCount(2, "確定 2 件ぶん・1 件につき 1 行だけ（末尾の明細ループを戻すと 4 行になる）");
        criticals.Count(e => e.Message.Contains(first.ToString(), StringComparison.Ordinal)).Should().Be(1);
        criticals.Count(e => e.Message.Contains(second.ToString(), StringComparison.Ordinal)).Should().Be(1);

        await host.StopAsync();
    }

    // ---- 🔴 T-10-1078, FR-10, #853, IADR-0428 決定4: 保護の結果を種類ごとに記録し、イベントを発行する ----

    private sealed class StubProtection(Func<Guid, ReconciledEntryProtectionOutcome> fn) : IReconciledEntryProtection
    {
        public Task<ReconciledEntryProtectionOutcome> ProtectAsync(
            OrderExecutionService.Domain.ExecutionRecord confirmed, CancellationToken cancellationToken) =>
            Task.FromResult(fn(confirmed.DecisionId));
    }

    private static async Task<(RecordingLogger Logger, ITrackedSession Session, Guid DecisionId)> ReconcileWithAsync(
        IReconciledEntryProtection? protection)
    {
        var reservations = new InMemoryOrderReservationStore();
        var decisionId = Guid.NewGuid();
        reservations.TryReserve(decisionId, StalledAt);
        var host = await BuildHostAsync(
            new StubProbe(ReservationProbeResult.Placed(Placed("BRK-P"))), reservations, protection: protection);
        var logger = new RecordingLogger();
        var service = new OrderReservationReconciliationService(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<IWolverineRuntime>(),
            host.Services.GetRequiredService<IClock>(),
            Options.Create(new ReconciliationOptions { Enabled = true }),
            new BusinessMetrics(),
            logger);

        Func<IMessageContext, Task> reconcile = async _ => await service.ReconcileOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(reconcile);
        await host.StopAsync();
        host.Dispose();
        return (logger, session, decisionId);
    }

    [Fact]
    public async Task 突合で確定したエントリーに張った保護逆指値を記録し発行する()
    {
        var stopDecisionId = Guid.NewGuid();
        var (logger, session, decisionId) = await ReconcileWithAsync(new StubProtection(id =>
            new ReconciledEntryProtectionOutcome(id, ReconciledEntryProtectionKind.BrokerStopPlaced,
            [
                new ProtectiveStopPlaced(id, stopDecisionId, "stop-9",
                    new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
                        BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close), 950m, 1, Now),
            ])));

        session.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle(m => m.DecisionId == decisionId);
        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().ContainSingle()
            .Which.StopDecisionId.Should().Be(stopDecisionId, "保護の結果のイベントは台帳・通知・監査へ発行する");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning
            && e.Message.Contains("保護逆指値を張りました", StringComparison.Ordinal)
            && e.Message.Contains(decisionId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task 突合で確定したものに保護の記録が無ければCriticalで知らせる_否定形()
    {
        var (logger, session, decisionId) = await ReconcileWithAsync(new StubProtection(id =>
            ReconciledEntryProtectionOutcome.Of(id, ReconciledEntryProtectionKind.NoProtectionRecord)));

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Critical
            && e.Message.Contains("保護の記録がありません", StringComparison.Ordinal)
            && e.Message.Contains(decisionId.ToString(), StringComparison.Ordinal));
        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().BeEmpty();
    }

    [Fact]
    public async Task 保護レグの突合は保護の記録が無いとCriticalで言わない()
    {
        // T-10-1078（続き・PR #1005 監査 5）: 据え置いた逆指値・ガードの成行手仕舞いの突合を「保護の記録が無い」と誤って知らせない。
        var (logger, _, decisionId) = await ReconcileWithAsync(new StubProtection(id =>
            ReconciledEntryProtectionOutcome.Of(id, ReconciledEntryProtectionKind.ProtectiveLeg)));

        logger.Entries.Should().NotContain(e => e.Message.Contains("保護の記録がありません", StringComparison.Ordinal));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information
            && e.Message.Contains("保護レグ（据え置いた逆指値・成行手仕舞い）", StringComparison.Ordinal)
            && e.Message.Contains(decisionId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task 保護の口が無い構成でも突合で確定したものはCriticalで知らせる_否定形()
    {
        var (logger, _, decisionId) = await ReconcileWithAsync(protection: null);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Critical
            && e.Message.Contains("保護レグを張る口がありません", StringComparison.Ordinal)
            && e.Message.Contains(decisionId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task 保護の口が落ちたらCriticalで知らせ巡回は続ける_否定形()
    {
        var (logger, session, decisionId) = await ReconcileWithAsync(new StubProtection(
            _ => throw new InvalidOperationException("保護の口が落ちた（テスト）")));

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Critical
            && e.Message.Contains("保護レグを張る処理が失敗しました", StringComparison.Ordinal)
            && e.Message.Contains(decisionId.ToString(), StringComparison.Ordinal));
        session.Sent.MessagesOf<OrderExecuted>().Should().ContainSingle(m => m.DecisionId == decisionId,
            "確定済みの OrderExecuted は保護の失敗より先に出ている（IADR-0371）");
        logger.Entries.Should().Contain(e => e.Message.Contains("滞留 1 件を走査", StringComparison.Ordinal),
            "巡回は最後まで回る（保護の失敗で残りの突合を止めない）");
    }

    // ---- FR-05, NFR-09, #856, IADR-0441: 判定の内訳を業務メトリクスで数える（ログの grep ではなく Prometheus で数えられるように） ----

    // 予約ごとに照会の答えを変えるプローブ（未登録の予約は Indeterminate。例外を投げる答えも差し込める）。
    private sealed class MapProbe(Dictionary<Guid, Func<ReservationProbeResult>> answers) : IReservationBrokerProbe
    {
        public Task<ReservationProbeResult> ProbeAsync(
            OrderDispatchReservation reservation, CancellationToken cancellationToken = default) =>
            Task.FromResult(answers.TryGetValue(reservation.DecisionId, out var answer)
                ? answer()
                : ReservationProbeResult.Indeterminate);
    }

    private static Dictionary<string, double> SumByOutcome(MeterCapture capture) =>
        capture.ValuesOf(BusinessMetricNames.OrderReservationReconciliations)
            .GroupBy(m => m.Tags[BusinessMetricNames.TagOutcome])
            .ToDictionary(g => g.Key, g => g.Sum(m => m.Value));

    [Fact]
    public async Task 巡回は判定を内訳ごとに数え_確定は1件ずつ_据え置きと不確定と失敗は件数で数える()
    {
        // T-10-1560: 巡回サマリの内訳と 1 対 1（①自己修復と②照会で確定は分ける）。解放は門が閉じているので 0（計上しない）。
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        var metrics = BusinessMetrics.WithMeterName(meterName);

        var reservations = new InMemoryOrderReservationStore();
        var placed = Guid.NewGuid();
        var selfHealed = Guid.NewGuid();
        var notPlaced = Guid.NewGuid();
        var indeterminate = Guid.NewGuid();
        var failing = Guid.NewGuid();
        foreach (var id in new[] { placed, selfHealed, notPlaced, indeterminate, failing })
            reservations.TryReserve(id, StalledAt);

        using var host = await BuildHostAsync(new MapProbe(new()
        {
            [placed] = () => ReservationProbeResult.Placed(Placed("BRK-M1")),
            [notPlaced] = () => ReservationProbeResult.NotPlaced,
            [failing] = () => throw new InvalidOperationException("照会が落ちた（テスト）"),
        }), reservations);
        host.Services.GetRequiredService<IExecutedOrderStore>().Save(new OrderExecutionService.Domain.ExecutionRecord(
            selfHealed, "BRK-M2", "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, PositionEffect.Open,
            10, 100m, 10, 100m, OrderStatus.Filled, 0m, StalledAt));
        var service = BuildService(host, new ReconciliationOptions { Enabled = true }, metrics);

        var result = await service.ReconcileOnceAsync(CancellationToken.None);

        result.Scanned.Should().Be(5, "前提: 5 件とも滞留として走査される");
        SumByOutcome(capture).Should().BeEquivalentTo(new Dictionary<string, double>
        {
            [BusinessMetrics.ReservationReconciliationProbePlaced] = 1,
            [BusinessMetrics.ReservationReconciliationSelfHealed] = 1,
            [BusinessMetrics.ReservationReconciliationHeldNotPlaced] = 1,
            [BusinessMetrics.ReservationReconciliationIndeterminate] = 1,
            [BusinessMetrics.ReservationReconciliationFailed] = 1,
        }, "解放の門が閉じているので released は 1 件も計上されない");
    }

    [Fact]
    public async Task 巡回は確定した1件も巡回サマリも予約の取引環境で数える()
    {
        // 🔴 T-10-1613, NFR-09, ADR-0045 決定1・決定2, #1051, IADR-0444 決定6: (a) probe-placed と (b) held-not-placed は
        // **取引環境ごとに**示す。確定した 1 件の出口（EmitAsync）と巡回サマリ（据え置き・不確定・失敗）の両方が、
        // 照会先ではなく**予約の取引環境**をタグ provider に載せる。取引環境が不明な予約は Unknown と数える。
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        var metrics = BusinessMetrics.WithMeterName(meterName);

        var reservations = new InMemoryOrderReservationStore();
        var simulatePlaced = Guid.NewGuid();
        var realHeld = Guid.NewGuid();
        var unknownIndeterminate = Guid.NewGuid();
        var simulateFailing = Guid.NewGuid();
        reservations.TryReserve(simulatePlaced, StalledAt, BrokerProvider.MoomooSimulate);
        reservations.TryReserve(realHeld, StalledAt.AddSeconds(1), BrokerProvider.MoomooReal);
        reservations.TryReserve(unknownIndeterminate, StalledAt.AddSeconds(2));
        reservations.TryReserve(simulateFailing, StalledAt.AddSeconds(3), BrokerProvider.MoomooSimulate);

        using var host = await BuildHostAsync(new MapProbe(new()
        {
            [simulatePlaced] = () => ReservationProbeResult.Placed(Placed("BRK-E1")),
            [realHeld] = () => ReservationProbeResult.NotPlaced,
            [simulateFailing] = () => throw new InvalidOperationException("照会が落ちた（テスト）"),
        }), reservations);
        var service = BuildService(host, new ReconciliationOptions { Enabled = true }, metrics);

        var result = await service.ReconcileOnceAsync(CancellationToken.None);

        result.Scanned.Should().Be(4, "前提: 4 件とも滞留として走査される");
        capture.ValuesOf(BusinessMetricNames.OrderReservationReconciliations)
            .Select(m => (m.Tags[BusinessMetricNames.TagOutcome], m.Tags[BusinessMetricNames.TagProvider], m.Value))
            .Should().BeEquivalentTo(new[]
            {
                (BusinessMetrics.ReservationReconciliationProbePlaced, "MoomooSimulate", 1d),
                (BusinessMetrics.ReservationReconciliationHeldNotPlaced, "MoomooReal", 1d),
                (BusinessMetrics.ReservationReconciliationIndeterminate, BusinessMetrics.ReservationReconciliationProviderUnknown, 1d),
                (BusinessMetrics.ReservationReconciliationFailed, "MoomooSimulate", 1d),
            }, "照会先（本構成は内蔵 paper）ではなく、予約の取引環境で数える");
    }

    [Fact]
    public async Task 発行が落ちても確定した1件の計数は発行より先に残る()
    {
        // 🔴 T-10-1561（否定形）: 確定した予約は次の巡回に載らない（IADR-0371）。計数を発行の後ろに置くと、
        // 発行が落ちた 1 件は永久に数えられない。記録（Critical）と同じく発行より先に数える。
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);

        var reservations = new InMemoryOrderReservationStore();
        reservations.TryReserve(Guid.NewGuid(), StalledAt);
        using var live = await BuildHostAsync(
            new StubProbe(ReservationProbeResult.Placed(Placed("BRK-M3"))), reservations);
        var brokenBus = await BuildHostAsync(
            new IndeterminateReservationBrokerProbe(), new InMemoryOrderReservationStore());
        var deadRuntime = brokenBus.Services.GetRequiredService<IWolverineRuntime>();
        await brokenBus.StopAsync();
        brokenBus.Dispose();

        var service = new OrderReservationReconciliationService(
            live.Services.GetRequiredService<IServiceScopeFactory>(),
            deadRuntime,
            live.Services.GetRequiredService<IClock>(),
            Options.Create(new ReconciliationOptions { Enabled = true }),
            BusinessMetrics.WithMeterName(meterName),
            NullLogger<OrderReservationReconciliationService>.Instance);

        var publish = async () => await service.ReconcileOnceAsync(CancellationToken.None);

        await publish.Should().ThrowAsync<Exception>("前提: 発行は落ちる");
        SumByOutcome(capture).Should().BeEquivalentTo(
            new Dictionary<string, double> { [BusinessMetrics.ReservationReconciliationProbePlaced] = 1 },
            "確定した 1 件は発行の前に数えられている（巡回サマリの件数は巡回が回りきらないので計上されない）");
    }
}
