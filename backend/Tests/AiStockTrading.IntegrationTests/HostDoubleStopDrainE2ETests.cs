using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 NFR, IADR-0591 決定 1, MSP#2265: **in-process のホストの二重の停止で、排出の途中にコンテナが破棄されないこと**を実ブローカで確かめる。
//
// in-process のホストでは、停止が 2 回並んで呼ばれる（WebApplicationFactory.DisposeAsync 由来と、エントリポイントの RunAsync 由来）。
// コンテナの破棄は RunAsync の側が行う。Wolverine の 2 回目の停止が排出の完了を待たずに終わると、RunAsync が排出の途中で
// RabbitMQ の接続を破棄し、リスナのチャネルを閉じる待ちが永久に止まる（MSP の定時実行 run 40229948691 と手元の heap dump で実測）。
//
// 窓を決定的に開くため、処理中のメッセージを 1 通、排出の最中に残す: 発注執行が送る直前に照会する停止状態の照会元を、
// 「入ったことを知らせてから数秒待つ」差し替えにする。排出はこの処理の完了を待つので、窓（排出の最中）は毎回数秒開く。
// 表明は「コンテナの破棄は、処理中のメッセージの完了より後である」。gate（束ねた停止）が無いと、2 回目の停止が排出を待たずに
// 終わり、RunAsync が処理の最中にコンテナを破棄する（手元の実測: 停止の開始から約 70 ms で破棄、処理の完了はその約 4 秒後）。
// 永久に止まるかどうかはリスナの閉じる待ちとの交差しだいで、まれにしか起きない。だが**破棄が排出より先に来ること**は、2 本の停止のうち
// エントリポイント側が後から Wolverine に着いた回には毎回起きる（gate を外した変異で、手元では大半の回で赤。率は作業仕様書・PR の表）。
[Trait("Category", "Integration")]
public sealed class HostDoubleStopDrainE2ETests : IAsyncLifetime
{
    private const string ExecutionServiceName = "ai-stock-trading.order-execution-service";
    private static readonly TimeSpan InFlight = TimeSpan.FromSeconds(4);

    private readonly PostgreSqlContainer? _postgres;
    private readonly RabbitMqContainer? _rabbitMq;
    private readonly SlowHaltSource _halt = new();
    private readonly ContainerDisposalProbe _disposal = new();
    private WebApplicationFactory<Program>? _factory;
    private string _rabbitMqConnection = string.Empty;

    public HostDoubleStopDrainE2ETests()
    {
        // NFR, IADR-0497 (#1200): 要る依存を得られなければ、コンテナを組み立てる前に理由つきで skip する。
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.RabbitMq);
        _postgres = E2EInfrastructure.UseExternal ? null : new PostgreSqlBuilder("postgres:16").Build();
        _rabbitMq = E2EInfrastructure.UseExternal ? null : new RabbitMqBuilder("rabbitmq:3.13-management").Build();
    }

    // 処理中のメッセージを排出の最中に残す照会元。入ったことを知らせ、取り消しを無視して決まった時間だけ待つ。
    private sealed class SlowHaltSource : ITradingHaltSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset? FinishedAt { get; private set; }

        public async Task<TradingHaltState?> GetAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(InFlight, CancellationToken.None);
            FinishedAt = DateTimeOffset.UtcNow;
            return new TradingHaltState(KillSwitchEngaged: false, TradingPaused: false);
        }
    }

    // コンテナ（DI）が破棄された時刻を残す。RunAsync の Host.DisposeAsync がコンテナを破棄すると呼ばれる。
    private sealed class ContainerDisposalProbe : IAsyncDisposable
    {
        public DateTimeOffset? DisposedAt { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposedAt ??= DateTimeOffset.UtcNow;
            return ValueTask.CompletedTask;
        }
    }

    public async ValueTask InitializeAsync()
    {
        try
        {
            if (_postgres is not null && _rabbitMq is not null)
                await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

            _rabbitMqConnection = E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString();
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
                E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
            Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", _rabbitMqConnection);
            Environment.SetEnvironmentVariable("Otlp__Endpoint", "http://localhost:4317");
            Environment.SetEnvironmentVariable("Broker__Provider", "paper");

            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("IntegrationTest");
                builder.ConfigureTestServices(services =>
                {
                    services.AddScoped<ITradingHaltSource>(_ => _halt);
                    // インスタンスを渡すとコンテナは破棄しないので、工場で作らせて破棄させる。
                    services.AddSingleton(_ => _disposal);
                });
            });
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // 本試験の本体が破棄を済ませている。済んでいなければ（前段で落ちた等）ここで破棄する。
            await E2EInfrastructure.DisposeQuietlyAsync(_factory, "発注執行のホスト");
        }
        finally
        {
            var disposals = new List<Task>();
            if (_postgres is not null)
                disposals.Add(_postgres.DisposeAsync().AsTask());
            if (_rabbitMq is not null)
                disposals.Add(_rabbitMq.DisposeAsync().AsTask());
            try
            {
                await Task.WhenAll(disposals);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
                Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", null);
                Environment.SetEnvironmentVariable("Otlp__Endpoint", null);
                Environment.SetEnvironmentVariable("Broker__Provider", null);
            }
        }
    }

    [Fact]
    public async Task P1_処理中のメッセージを残したままホストを破棄しても_二重の停止は排出を待ち_破棄は終わる()
    {
        var factory = _factory!;
        using (var client = factory.CreateClient())
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).IsSuccessStatusCode
                   && DateTime.UtcNow < deadline)
                await Task.Delay(500, TestContext.Current.CancellationToken);
        }

        factory.Services.GetRequiredService<ContainerDisposalProbe>().Should().BeSameAs(_disposal);

        var queue = await RabbitMqTopologyProbe.WaitForQueueAsync(
            _rabbitMqConnection, WolverineExtensions.QueueNameFor(ExecutionServiceName, typeof(OrderApproved)),
            minConsumers: 1, TimeSpan.FromSeconds(30));
        queue.Should().NotBeNull("購読が始まってから発行する");

        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 1, 20m,
            StopLossPrice: 18m); // FR-10, #331: Open は損切り価格が必須
        await OrderExecutionPipelineE2ETests.PublishAsync(
            factory.Services, new OrderApproved(Guid.NewGuid(), intent, ApprovedQuantity: 1, ApprovedAt: DateTimeOffset.UtcNow));

        // 処理中に入った（＝排出がこの処理の完了を待つ）ことを確かめてから、ホストを破棄する。
        (await Task.WhenAny(_halt.Entered.Task, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)))
            .Should().BeSameAs(_halt.Entered.Task, "承認注文の処理が停止状態の照会まで進む");

        // 束ねが無いと、ここで排出の途中にコンテナ（RabbitMQ の接続）が破棄され、破棄は二度と終わらない（上限で名指して落ちる）。
        await E2EInfrastructure.DisposeQuietlyAsync(factory, "発注執行のホスト", TimeSpan.FromSeconds(60));
        _factory = null;

        _halt.FinishedAt.Should().NotBeNull("排出は処理中のメッセージの完了を待つ");
        _disposal.DisposedAt.Should().NotBeNull("コンテナは破棄される");
        _disposal.DisposedAt!.Value.Should().BeOnOrAfter(_halt.FinishedAt!.Value,
            "コンテナの破棄は排出（処理中のメッセージの完了）の後でなければならない。先に来るのは、2 回目の停止が排出を待たずに終わった形（MSP#2265）");
    }
}
