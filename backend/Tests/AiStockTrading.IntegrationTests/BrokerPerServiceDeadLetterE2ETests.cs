using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 NFR-18, IADR-0635: **デッドレターはサービスごと。共有のデッドレターの権限が無い利用者でも、起動し、失敗したメッセージは自分の DLQ へ移る。**
//
// 実ブローカ（Testcontainers の RabbitMQ 3.13）で、基盤の生成器がクラウドの購読側に与える形の権限（自分の接頭辞・応答キュー・既定の
// exchange・購読の exchange の read。共有の wolverine-dead-letter-queue は含めない）の利用者を作り、
//   1. 購読側は起動し、自分の既定のデッドレター（<ServiceName>.dead-letter）と受信キューの DLQ（<queue>_error）を宣言する。
//      共有の名前のキューはブローカに作られない
//   2. 処理に失敗し続けたメッセージは、共通の再試行（2s・10s・30s）を使い切った後に <queue>_error へ移る
//   3. その利用者は共有の名前のキューを宣言できない（＝共有の名前を宣言する配線のままなら起動できない。権限を外せた）
// を確かめる。利用者を作るためにブローカの管理コマンドを使うので、外部注入（E2E_*）の経路では走らない。
[Trait("Category", "Integration")]
public sealed class BrokerPerServiceDeadLetterE2ETests : IAsyncLifetime
{
    private const string SubscriberService = "ai-stock-trading.dlq-e2e-subscriber";
    private const string PublisherService = "ai-stock-trading.dlq-e2e-publisher";
    private const string SubscriberUser = "dlq-e2e-subscriber";

    private static readonly string Exchange = typeof(DlqProbeFailing).FullName!;

    private readonly RabbitMqContainer _rabbitMq;
    private readonly string _subscriberPassword = Guid.NewGuid().ToString("N");

    public BrokerPerServiceDeadLetterE2ETests()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.RabbitMq);
        Assert.SkipWhen(E2EInfrastructure.UseExternal,
            "利用者と権限をブローカの管理コマンドで作るため、外部注入（E2E_*）の経路では走らない。Testcontainers の経路で走らせること。");

        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management").Build();
    }

    public async ValueTask InitializeAsync()
    {
        await _rabbitMq.StartAsync();
        DlqProbeFailingReceiver.Reset();

        await RabbitMqCtlAsync("add_user", SubscriberUser, _subscriberPassword);
        // 基盤の生成器（gen-rabbitmq-permissions.js）がクラウドの購読側に与える形。共有のデッドレターは無い。
        var own = Regex.Escape($"{SubscriberService}.") + ".*";
        const string response = @"wolverine\.response\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
        await RabbitMqCtlAsync(
            "set_permissions", "-p", "/", SubscriberUser,
            $"^({own}|{response})$",
            $"^({own}|amq\\.default)$",
            $"^({own}|{response}|{Regex.Escape(Exchange)})$");

        // 運用（基盤のブローカの起動の直後の初期化）が購読される exchange を先に宣言する（クラウドの形）。
        var factory = new ConnectionFactory { Uri = new Uri(_rabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
    }

    public async ValueTask DisposeAsync()
    {
        DlqProbeFailingReceiver.Reset();
        await _rabbitMq.DisposeAsync();
    }

    private async Task RabbitMqCtlAsync(params string[] args)
    {
        var result = await _rabbitMq.ExecAsync(["rabbitmqctl", .. args]);
        result.ExitCode.Should().Be(0, $"rabbitmqctl {args[0]} が失敗した: {result.Stderr}");
    }

    private string SubscriberConnectionString() =>
        new UriBuilder(_rabbitMq.GetConnectionString()) { UserName = SubscriberUser, Password = _subscriberPassword }.Uri.ToString();

    [Fact]
    public async Task 共有のデッドレターの権限が無い利用者でも起動し失敗したメッセージは自分のDLQへ移る()
    {
        var subscriber = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(SubscriberService, SubscriberConnectionString(), SubscribedExchangeDeclaration.Passive);
                opts.Discovery.IncludeType<DlqProbeFailingReceiver>();
            })
            .StartAsync();
        var publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
                PublisherService, _rabbitMq.GetConnectionString(), SubscribedExchangeDeclaration.Declare))
            .StartAsync();
        try
        {
            var admin = _rabbitMq.GetConnectionString();
            var queue = WolverineExtensions.QueueNameFor(SubscriberService, typeof(DlqProbeFailing));
            var errorQueue = WolverineExtensions.DeadLetterQueueNameFor(queue);
            (await RabbitMqTopologyProbe.WaitForQueueAsync(admin, queue, 1, TimeSpan.FromSeconds(30)))
                .Should().NotBeNull("購読側は共有のデッドレターの権限なしで起動し、自分のキューに consumer を付ける");

            // 1. サービスごとのデッドレターを宣言し、共有の名前は作らない。
            (await RabbitMqTopologyProbe.TryDescribeQueueAsync(admin, WolverineExtensions.ServiceDeadLetterQueueNameFor(SubscriberService)))
                .Should().NotBeNull("transport の既定の DLQ はサービスごとの名前で宣言される");
            (await RabbitMqTopologyProbe.TryDescribeQueueAsync(admin, errorQueue)).Should().NotBeNull("受信キューの DLQ は <queue>_error");
            (await RabbitMqTopologyProbe.TryDescribeQueueAsync(admin, RabbitMqTransport.DeadLetterQueueName))
                .Should().BeNull("共有の名前のキューはどのサービスも宣言しない（発行側も含む）");

            // 2. 失敗し続けるメッセージは再試行を使い切った後に <queue>_error へ移る。
            await new MessageBus(publisher.Services.GetRequiredService<IWolverineRuntime>()).PublishAsync(new DlqProbeFailing("dead"));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
            RabbitMqTopologyProbe.QueueState? moved = null;
            while (DateTime.UtcNow < deadline)
            {
                moved = await RabbitMqTopologyProbe.TryDescribeQueueAsync(admin, errorQueue);
                if (moved is { MessageCount: >= 1 }) break;
                await Task.Delay(1000);
            }

            moved!.MessageCount.Should().Be(1, "再試行を使い切ったメッセージは自分の <queue>_error へ移る");
            DlqProbeFailingReceiver.Attempts.Should().Be(WolverineExtensions.MaxDeliveryAttempts, "共通の再試行を使い切ってから移る");
            (await RabbitMqTopologyProbe.TryDescribeQueueAsync(admin, queue))!.MessageCount.Should().Be(0u, "受信キューに残らない");
            (await RabbitMqTopologyProbe.TryDescribeQueueAsync(admin, RabbitMqTransport.DeadLetterQueueName))
                .Should().BeNull("共有の名前のキューへは移らない");
        }
        finally
        {
            await E2EInfrastructure.DisposeQuietlyAsync(publisher as IAsyncDisposable, "発行側のホスト");
            await E2EInfrastructure.DisposeQuietlyAsync(subscriber as IAsyncDisposable, "購読側のホスト");
        }
    }

    [Fact]
    public async Task 利用者は共有の名前のデッドレターを宣言できない()
    {
        var factory = new ConnectionFactory { Uri = new Uri(SubscriberConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();

        // 自分の接頭辞の中のデッドレターは宣言できる。
        await using (var own = await connection.CreateChannelAsync())
        {
            await own.QueueDeclareAsync(WolverineExtensions.ServiceDeadLetterQueueNameFor(SubscriberService), durable: true, exclusive: false, autoDelete: false);
        }

        // 共有の名前は宣言できない —— 共有の名前を宣言する配線（Wolverine の既定）のままでは、この権限では起動できない。
        await using var shared = await connection.CreateChannelAsync();
        var act = () => shared.QueueDeclareAsync(RabbitMqTransport.DeadLetterQueueName, durable: true, exclusive: false, autoDelete: false);
        (await act.Should().ThrowAsync<OperationInterruptedException>())
            .Which.ShutdownReason!.ReplyCode.Should().Be(403, "共有のデッドレターの configure を持たない");
    }
}

/// <summary>NFR-18, IADR-0635: 本試験の型（入れ子にしない＝ exchange の名前が型の完全名になる）。</summary>
public sealed record DlqProbeFailing(string Id);

/// <summary>
/// 本試験の受け手。必ず失敗する。名前を Wolverine の規約（Handler / Consumer の接尾辞）から外し、購読側のホストだけが取り込む。
/// </summary>
public sealed class DlqProbeFailingReceiver
{
    private static int _attempts;

    public static int Attempts => Volatile.Read(ref _attempts);

    public static void Reset() => Interlocked.Exchange(ref _attempts, 0);

    public void Handle(DlqProbeFailing message)
    {
        Interlocked.Increment(ref _attempts);
        throw new InvalidOperationException($"NFR-18, IADR-0635: デッドレターへの移送を確かめるための失敗（{message.Id}）");
    }
}
