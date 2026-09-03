using OrderExecutionService.Infrastructure.Persistence;
using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Domain;
using OrderExecutionService.Hosted;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
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

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値ガードの**常駐**（巡回の駆動とイベント発行）の検証。
//
// 分岐そのもの（失効検知・再発注・残存取消・据え置き）は ProtectiveStopGuard の単体テストが持つ。
// ここで固定するのは常駐でしか壊れない 4 点である。
//   1. 巡回が得たイベントが**実際に発行される**こと。発行が欠けると、リスク管理が保護レグの承認行を
//      台帳へ追加できず、逆指値が約定しても台帳の建玉が減らない（IADR-0210 決定2）。
//   2. 無効化したときに**一度も巡回しない**こと（既定有効の裏返し。無効化は「失効を検知しない」選択である）。
//   3. 巡回が失敗しても**常駐が落ちず次回巡回で再試行する**こと。ここで落ちると、以後どの建玉の
//      逆指値失効も永久に検知されなくなる（fail-safe が静かに消える）。
//   4. 停止要求によるキャンセルを**失敗として記録しない**こと。通常の再起動のたびに警報が上がると、
//      本物の失敗が埋もれる。
public class ProtectiveStopGuardServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    // 逆指値の照会結果・建玉・再発注の可否を注入できるブローカ（ガードの単体テストと同じ流儀）。
    private sealed class GuardBroker : IBrokerAdapter, IProtectiveOrderBroker, IBrokerPositionSource
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public Dictionary<string, BrokerOrder?> Orders { get; } = new();

        public IReadOnlyList<BrokerPositionSnapshot>? Positions { get; set; } = [];

        public bool RejectReplacement { get; set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("ガードは通常発注を行わない");

        public Task<BrokerOrder> PlaceStopOrderAsync(
            OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "stop-re-1", closeIntent, RejectReplacement ? OrderStatus.Rejected : OrderStatus.Accepted,
                0, 0m, Now, RejectReplacement ? Now : null));

        public Task<BrokerOrder> PlaceMarketOrderAsync(
            OrderIntent closeIntent, Guid decisionId, CancellationToken ct = default) =>
            Task.FromResult(new BrokerOrder(
                "close-1", closeIntent, OrderStatus.Filled, closeIntent.Quantity, closeIntent.Price, Now, Now));

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var order) ? order : null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult(Positions);
    }

    // 巡回の呼び出し回数を数え、必要なら例外へ倒せるストア（常駐の再試行・非巡回を観測するため）。
    private sealed class CountingStopStore(IProtectiveStopOrderStore inner) : IProtectiveStopOrderStore
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }

        /// <summary>この回数に達するまでの FindActive は例外に倒す（0＝常に成功）。</summary>
        public int FailUntilCall { get; init; }

        /// <summary>FindActive がこの回数に達したら完了するシグナル。</summary>
        public int SignalOnCall { get; init; } = 1;

        public Task Reached => _reached.Task;

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize)
        {
            Calls++;
            if (Calls >= SignalOnCall) _reached.TrySetResult();
            if (Calls <= FailUntilCall)
                throw new InvalidOperationException("巡回対象の照会に失敗（テスト）");
            return inner.FindActive(batchSize);
        }

        public void Save(ProtectiveStopOrder stop) => inner.Save(stop);

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => inner.Find(entryDecisionId);
    }

    // 巡回の最中に停止要求が伝播した状況（キャンセル）を再現するストア。
    private sealed class CancellingStopStore : IProtectiveStopOrderStore
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize)
        {
            _reached.TrySetResult();
            throw new OperationCanceledException("停止要求で巡回を中断（テスト）");
        }

        public void Save(ProtectiveStopOrder stop) { }

        public ProtectiveStopOrder? Find(Guid entryDecisionId) => null;
    }

    // ログを記録するロガー。常駐（BackgroundService）の ExecuteAsync は StartAsync とは別のタスクで
    // 走るため、固定の待ち時間ではなく**最初の記録をシグナルで待つ**（時間依存のちらつきを作らない）。
    private sealed class RecordingLogger : ILogger<ProtectiveStopGuardService>
    {
        private readonly TaskCompletionSource _logged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public Task Logged => _logged.Task;

        public IEnumerable<string> Warnings =>
            Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public IEnumerable<string> Errors =>
            Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
            _logged.TrySetResult();
        }
    }

    private static ProtectiveStopOrder ActiveStop() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, 1,
            ProtectiveStopState.Active, Now.AddMinutes(-5), Now.AddMinutes(-5));

    private static BrokerOrder StopOrder(OrderStatus status) =>
        new("stop-1", new OrderIntent("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            BrokerProvider.MoomooSimulate, 10, 950m, PositionEffect.Close), status, 0, 0m, Now,
            OrderStatusLifecycle.IsTerminal(status) ? Now : null);

    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // 本番と同じ配線（キュー名・fan-out・再試行・DLQ）を用い、送信先だけ stub へ倒す。
    private static Task<IHost> BuildHostAsync(GuardBroker broker, IProtectiveStopOrderStore stops) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IClock, FakeClock>();
                opts.Services.AddSingleton<IExecutedOrderStore>(new InMemoryExecutedOrderStore());
                opts.Services.AddSingleton(stops);
                opts.Services.AddScoped(sp => new ProtectiveStopGuard(
                    broker, broker, sp.GetRequiredService<IProtectiveStopOrderStore>(),
                    sp.GetRequiredService<IExecutedOrderStore>(), sp.GetRequiredService<IClock>()));

                opts.UseAiStockTradingRabbitMq(ServiceName, "amqp://guest:guest@localhost:5672");
                opts.StubAllExternalTransports();
            })
            .StartAsync();

    private static ProtectiveStopGuardService BuildService(
        IHost host, ProtectiveStopGuardOptions options, ILogger<ProtectiveStopGuardService>? logger = null) =>
        new(host.Services.GetRequiredService<IServiceScopeFactory>(),
            // 常駐（singleton）であり、Wolverine の IMessageBus（scoped）は注入できない。
            host.Services.GetRequiredService<IWolverineRuntime>(),
            Options.Create(options),
            logger ?? NullLogger<ProtectiveStopGuardService>.Instance);

    // ---- 巡回結果の発行 ----

    [Fact]
    public async Task 再発注した保護逆指値はProtectiveStopPlacedとして発行される()
    {
        // 逆指値が失効（Cancelled）し建玉が残っている＝ガードが再発注する状況。
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(ActiveStop());
        var broker = new GuardBroker { Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)] };
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Cancelled);

        using var host = await BuildHostAsync(broker, stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions());

        ProtectiveStopGuardResult result = null!;
        Func<IMessageContext, Task> run = async _ => result = await service.RunOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(run);

        result.Replaced.Should().Be(1);
        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().ContainSingle(m => m.Attempt == 2);

        await host.StopAsync();
    }

    [Fact]
    public async Task 再発注できず手仕舞った場合はProtectiveStopCoverageLostとして発行される()
    {
        // 再発注が拒否され成行で手仕舞う＝「逆指値なしの建玉を持たない」対処が働いた状況。
        // 発行されないと監査・Critical 通知の双方が沈黙し、建玉が消えた理由を誰も知れない。
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(ActiveStop());
        var broker = new GuardBroker
        {
            Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)],
            RejectReplacement = true,
        };
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Rejected);

        using var host = await BuildHostAsync(broker, stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions());

        ProtectiveStopGuardResult result = null!;
        Func<IMessageContext, Task> run = async _ => result = await service.RunOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(run);

        result.ClosedOut.Should().Be(1);
        session.Sent.MessagesOf<ProtectiveStopCoverageLost>().Should().ContainSingle(m =>
            m.Cause == ProtectiveStopLossCause.LapsedInFlight
            && m.Remediation == ProtectiveStopRemediation.PositionClosed);

        await host.StopAsync();
    }

    [Fact]
    public async Task 維持だけの巡回では何も発行しない_否定形()
    {
        // 建玉あり・逆指値滞留中は正常な状態である。ここで何かを発行すると、通知が平常時に鳴り続けて
        // 実際の保護喪失（Critical）が埋もれる。
        var stops = new InMemoryProtectiveStopOrderStore();
        stops.Save(ActiveStop());
        var broker = new GuardBroker { Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)] };
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);

        using var host = await BuildHostAsync(broker, stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions());

        ProtectiveStopGuardResult result = null!;
        Func<IMessageContext, Task> run = async _ => result = await service.RunOnceAsync(CancellationToken.None);
        var session = await host.TrackActivityForTest().ExecuteAndWaitAsync(run);

        result.StillActive.Should().Be(1);
        session.Sent.MessagesOf<ProtectiveStopPlaced>().Should().BeEmpty();
        session.Sent.MessagesOf<ProtectiveStopCoverageLost>().Should().BeEmpty();

        await host.StopAsync();
    }

    // ---- 常駐の駆動 ----

    [Fact]
    public async Task 無効化されていれば一度も巡回しない()
    {
        // 既定有効の裏返し。無効化は「逆指値の失効を検知しない」ことを明示的に選ぶ運用判断であり、
        // 🔴 **黙って無効になってはならない**——設定ミスで保護が消えたことに誰も気付けなくなる。
        var stops = new CountingStopStore(new InMemoryProtectiveStopOrderStore());
        var logger = new RecordingLogger();
        using var host = await BuildHostAsync(new GuardBroker(), stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions { Enabled = false }, logger);

        await service.StartAsync(CancellationToken.None);
        // ExecuteAsync は StartAsync とは別タスクで走るため、最初のログをシグナルで待つ。
        await logger.Logged.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        stops.Calls.Should().Be(0);
        logger.Warnings.Should().ContainSingle(w => w.Contains("無効"),
            "無効化は警告として記録される（逆指値なしの建玉が残り得ることを明示する）");

        await host.StopAsync();
    }

    [Fact]
    public async Task 有効なら間隔ごとに巡回する()
    {
        var inner = new InMemoryProtectiveStopOrderStore();
        inner.Save(ActiveStop());
        var stops = new CountingStopStore(inner) { SignalOnCall = 2 };
        var broker = new GuardBroker { Positions = [new BrokerPositionSnapshot("AAPL", Market.UnitedStates, 10, 1_000m)] };
        broker.Orders["stop-1"] = StopOrder(OrderStatus.Accepted);

        using var host = await BuildHostAsync(broker, stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions { Interval = TimeSpan.FromMilliseconds(10) });

        await service.StartAsync(CancellationToken.None);
        await stops.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        stops.Calls.Should().BeGreaterThanOrEqualTo(2, "巡回は間隔ごとに繰り返す");

        await host.StopAsync();
    }

    [Fact]
    public async Task 巡回が失敗しても常駐は落ちず次回巡回で再試行する()
    {
        // ここで常駐が落ちると、以後どの建玉の逆指値失効も検知されなくなる（保護が静かに消える）。
        var stops = new CountingStopStore(new InMemoryProtectiveStopOrderStore())
        {
            FailUntilCall = 1,
            SignalOnCall = 2,
        };
        using var host = await BuildHostAsync(new GuardBroker(), stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions { Interval = TimeSpan.FromMilliseconds(10) });

        await service.StartAsync(CancellationToken.None);
        await stops.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        stops.Calls.Should().BeGreaterThanOrEqualTo(2, "1 回目の例外で常駐は終了しない");

        await host.StopAsync();
    }

    [Fact]
    public async Task 停止要求で巡回が中断されてもエラーとして記録しない()
    {
        // 停止（デプロイ・再起動）でキャンセルが伝播した巡回は**異常ではない**。
        // これをエラーとして記録すると、通常の再起動のたびに警報が上がり、本物の失敗が埋もれる。
        var stops = new CancellingStopStore();
        var logger = new RecordingLogger();
        using var host = await BuildHostAsync(new GuardBroker(), stops);
        var service = BuildService(host, new ProtectiveStopGuardOptions(), logger);

        await service.StartAsync(CancellationToken.None);
        await stops.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        logger.Errors.Should().BeEmpty("キャンセルは失敗ではない");

        await host.StopAsync();
    }

    // ---- 設定の既定 ----

    [Fact]
    public void 既定は有効で巡回間隔と件数が定まっている()
    {
        // 既定無効だと「配線したのに何も守っていない」状態を設定ミスで作れてしまう（FR-10）。
        var options = new ProtectiveStopGuardOptions();

        options.Enabled.Should().BeTrue();
        options.Interval.Should().Be(TimeSpan.FromSeconds(30));
        options.BatchSize.Should().Be(50);
    }
}
