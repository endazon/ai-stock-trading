using System.Text.RegularExpressions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 NFR-18, IADR-0628: **購読側の利用者に、購読する exchange の configure を与えなくても、購読は動く**（クラウドの段 2 の権限の形）。
//
// 共通配線を Passive（クラウドの切り替え）で起こすと、購読側は購読の exchange を受動で宣言し（在るかの確かめだけ）、キューの宣言と
// 束ねは続ける。RabbitMQ の queue.bind に要るのはキューの write と exchange の read で、受動の宣言は configure を要らない。
// 本試験は実ブローカ（Testcontainers の RabbitMQ 3.13）で、基盤の生成器が購読側に与える形の権限（購読の exchange は read だけ）の
// 利用者を作り、
//   1. exchange を運用が先に宣言しておけば、購読側は起動し、束ね、発行側からのメッセージを受け取る
//   2. その利用者は購読の exchange を非受動で宣言できず、消せもしない（＝ Declare のままなら起動できない。configure を外せた）
//   3. 購読側が先に起動して exchange がまだ無いときは、受動の宣言が 404 で失敗したこと（初期化の失敗の記録）を拾い、その時点で購読の
//      キューが未宣言であることを確かめる。exchange が現れた後の再試行で束ねて受け取る（固定の時間の待ちに頼らない）
// を確かめる。利用者を作るためにブローカの管理コマンドを使うので、外部注入（E2E_*）の経路では走らない。
[Trait("Category", "Integration")]
public sealed partial class BrokerSubscriberLeastPrivilegeE2ETests : IAsyncLifetime
{
    private const string SubscriberService = "ai-stock-trading.bsc-e2e-subscriber";
    private const string PublisherService = "ai-stock-trading.bsc-e2e-publisher";
    private const string SubscriberUser = "bsc-e2e-subscriber";

    // 規約ルーティングの exchange 名はメッセージ型の完全名（IADR-0129 決定 2）。
    private static readonly string Exchange = typeof(BscLeastPrivilegeProbe).FullName!;

    private readonly RabbitMqContainer _rabbitMq;
    private readonly string _subscriberPassword = Guid.NewGuid().ToString("N");

    public BrokerSubscriberLeastPrivilegeE2ETests()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.RabbitMq);
        Assert.SkipWhen(E2EInfrastructure.UseExternal,
            "購読側の利用者と権限をブローカの管理コマンドで作るため、外部注入（E2E_*）の経路では走らない。Testcontainers の経路で走らせること。");

        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management").Build();
    }

    public async ValueTask InitializeAsync()
    {
        await _rabbitMq.StartAsync();
        BscLeastPrivilegeProbeReceiver.Reset();

        await RabbitMqCtlAsync("add_user", SubscriberUser, _subscriberPassword);
        // 基盤の生成器（gen-rabbitmq-permissions.js）がクラウドの購読側に与える形: 自分のキュー（接頭辞）・応答キュー・共有のデッドレター・
        // 既定の exchange（戻し）。購読の exchange は read だけ（configure も write も無い）。
        var own = Regex.Escape($"{SubscriberService}.") + ".*";
        const string response = @"wolverine\.response\..*";
        const string deadLetter = @"wolverine-dead-letter-queue";
        await RabbitMqCtlAsync(
            "set_permissions", "-p", "/", SubscriberUser,
            $"^({own}|{response}|{deadLetter})$",
            $"^({own}|{deadLetter}|amq\\.default)$",
            $"^({own}|{response}|{deadLetter}|{Regex.Escape(Exchange)})$");
    }

    public async ValueTask DisposeAsync()
    {
        BscLeastPrivilegeProbeReceiver.Reset();
        await _rabbitMq.DisposeAsync();
    }

    private async Task RabbitMqCtlAsync(params string[] args)
    {
        var result = await _rabbitMq.ExecAsync(["rabbitmqctl", .. args]);
        result.ExitCode.Should().Be(0, $"rabbitmqctl {args[0]} が失敗した: {result.Stderr}");
    }

    private string SubscriberConnectionString() =>
        new UriBuilder(_rabbitMq.GetConnectionString()) { UserName = SubscriberUser, Password = _subscriberPassword }.Uri.ToString();

    // 運用（基盤のブローカの起動の直後の初期化）が先に宣言するのと同じ形（Wolverine の AutoProvision と同じ: fanout・durable・自動削除しない）。
    private async Task DeclareExchangeAsOperatorAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(_rabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
    }

    private Task<IHost> StartSubscriberAsync(CancellationToken cancellationToken = default, ILoggerProvider? logs = null) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                if (logs is not null) logging.AddProvider(logs);
            })
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(SubscriberService, SubscriberConnectionString(), SubscribedExchangeDeclaration.Passive);
                // 受け手は本試験の型だけ（同じ試験アセンブリを使う他の試験のホストに、受け口を増やさない）。
                opts.Discovery.IncludeType<BscLeastPrivilegeProbeReceiver>();
            })
            .StartAsync(cancellationToken);

    // 発行側は管理者の利用者・従来の宣言（Declare）。発行側は自分の exchange を宣言し続ける。
    private Task<IHost> StartPublisherAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
                PublisherService, _rabbitMq.GetConnectionString(), SubscribedExchangeDeclaration.Declare))
            .StartAsync();

    private static async Task PublishAsync(IHost publisher, string id) =>
        await new MessageBus(publisher.Services.GetRequiredService<IWolverineRuntime>()).PublishAsync(new BscLeastPrivilegeProbe(id));

    [Fact]
    public async Task 購読のexchangeのconfigureが無い利用者でも運用が先に宣言すれば購読して受け取る()
    {
        await DeclareExchangeAsOperatorAsync();

        var subscriber = await StartSubscriberAsync();
        var publisher = await StartPublisherAsync();
        try
        {
            var queue = WolverineExtensions.QueueNameFor(SubscriberService, typeof(BscLeastPrivilegeProbe));
            (await RabbitMqTopologyProbe.WaitForQueueAsync(_rabbitMq.GetConnectionString(), queue, 1, TimeSpan.FromSeconds(30)))
                .Should().NotBeNull("購読側は自分のキューを宣言し、consumer を付ける");

            await PublishAsync(publisher, "after-operator-declare");
            (await BscLeastPrivilegeProbeReceiver.WaitForAsync("after-operator-declare", TimeSpan.FromSeconds(30)))
                .Should().BeTrue("購読側のキューは購読の exchange へ束ねられている");
        }
        finally
        {
            await E2EInfrastructure.DisposeQuietlyAsync(publisher as IAsyncDisposable, "発行側のホスト");
            await E2EInfrastructure.DisposeQuietlyAsync(subscriber as IAsyncDisposable, "購読側のホスト");
        }
    }

    [Fact]
    public async Task 購読側の利用者は購読のexchangeを宣言し直せず消せもしない()
    {
        await DeclareExchangeAsOperatorAsync();
        var factory = new ConnectionFactory { Uri = new Uri(SubscriberConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();

        // 受動の宣言（在るかの確かめ）は通る。
        await using (var passive = await connection.CreateChannelAsync())
        {
            await passive.ExchangeDeclarePassiveAsync(Exchange);
        }

        // 非受動の宣言（在っても configure が要る）は拒まれる —— Declare のままの購読側はこの権限では起動できない。
        await using (var declare = await connection.CreateChannelAsync())
        {
            var act = () => declare.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
            (await act.Should().ThrowAsync<OperationInterruptedException>())
                .Which.ShutdownReason!.ReplyCode.Should().Be(403, "購読の exchange の configure を持たない");
        }

        // 削除も拒まれる（configure は宣言と削除を区別しない。外したのはこの経路）。
        await using (var delete = await connection.CreateChannelAsync())
        {
            var act = () => delete.ExchangeDeleteAsync(Exchange);
            (await act.Should().ThrowAsync<OperationInterruptedException>())
                .Which.ShutdownReason!.ReplyCode.Should().Be(403, "購読側は発行側の exchange を消せない");
        }

        // 運用が宣言した exchange は残っている。
        await using var admin = await new ConnectionFactory { Uri = new Uri(_rabbitMq.GetConnectionString()) }.CreateConnectionAsync();
        await using var check = await admin.CreateChannelAsync();
        await check.ExchangeDeclarePassiveAsync(Exchange);
    }

    [Fact]
    public async Task 購読側が先に起動してもexchangeが現れるまで待ってから束ねて受け取る()
    {
        // exchange がまだ無い。購読側の起動は受動の宣言の 404 で止まり、Wolverine のブローカの初期化の再試行（5 秒おき）で待つ。
        // 🔴 固定の待ちに頼らない: 「受動の宣言が、この exchange の不在（404 NOT_FOUND）で失敗した」という初期化の失敗の記録を拾ってから
        //   次へ進む（待っている理由が exchange の不在であることを区別する）。期限は記録が出ないときの打ち切りだけに使う。
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var logs = new BrokerInitializationFailureCapture(Exchange);
        var starting = StartSubscriberAsync(limit.Token, logs);
        // 購読側の初期化が、受動の宣言がこの exchange の不在（404）で失敗したことを記録するまで待つ（出なければ全体の期限で赤）。
        await logs.ExchangeNotFound.WaitAsync(limit.Token);
        starting.IsCompleted.Should().BeFalse("exchange が無い間、購読側は起動を終えない");
        // exchange はキューより先に確かめるので、受動の宣言が失敗した時点で購読のキューはまだ宣言されていない（束ねの無いまま動かない）。
        (await RabbitMqTopologyProbe.TryDescribeQueueAsync(
                _rabbitMq.GetConnectionString(), WolverineExtensions.QueueNameFor(SubscriberService, typeof(BscLeastPrivilegeProbe))))
            .Should().BeNull("受動の宣言が通るまで、購読のキューの宣言と束ねに進まない");

        await DeclareExchangeAsOperatorAsync();
        var subscriber = await starting;
        var publisher = await StartPublisherAsync();
        try
        {
            await PublishAsync(publisher, "after-late-declare");
            (await BscLeastPrivilegeProbeReceiver.WaitForAsync("after-late-declare", TimeSpan.FromSeconds(30)))
                .Should().BeTrue("exchange が現れた後の再試行で束ね、受け取る");
        }
        finally
        {
            await E2EInfrastructure.DisposeQuietlyAsync(publisher as IAsyncDisposable, "発行側のホスト");
            await E2EInfrastructure.DisposeQuietlyAsync(subscriber as IAsyncDisposable, "購読側のホスト");
        }
    }
}

/// <summary>
/// NFR-18, IADR-0628: Wolverine のブローカの初期化の失敗の記録（再試行の前に Error で出る）のうち、例外が「この exchange の不在」
/// （RabbitMQ の 404 NOT_FOUND）であるものを拾う。拾ったら <see cref="ExchangeNotFound"/> が完了する。
/// </summary>
internal sealed class BrokerInitializationFailureCapture(string exchange) : ILoggerProvider
{
    private readonly TaskCompletionSource _notFound = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ExchangeNotFound => _notFound.Task;

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private void Observe(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is OperationInterruptedException { ShutdownReason.ReplyCode: 404 } oie
                && oie.ShutdownReason!.ReplyText.Contains(exchange, StringComparison.Ordinal))
            {
                _notFound.TrySetResult();
                return;
            }
        }
    }

    private sealed class Capture(BrokerInitializationFailureCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) owner.Observe(exception);
        }
    }
}

/// <summary>NFR-18, IADR-0628: 本試験の型（入れ子にしない＝ exchange の名前が型の完全名になる）。</summary>
public sealed record BscLeastPrivilegeProbe(string Id);

/// <summary>
/// 本試験の受け手。名前を Wolverine の規約（Handler / Consumer の接尾辞）から外し、購読側のホストだけが
/// <c>Discovery.IncludeType</c> で取り込む（同じ試験アセンブリを使う他のホストに受け口を作らない）。
/// </summary>
public sealed class BscLeastPrivilegeProbeReceiver
{
    private static readonly object Gate = new();
    private static readonly List<string> Received = [];

    public static void Reset()
    {
        lock (Gate) Received.Clear();
    }

    public static async Task<bool> WaitForAsync(string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (Gate)
            {
                if (Received.Contains(id)) return true;
            }

            await Task.Delay(200);
        }

        return false;
    }

    public void Handle(BscLeastPrivilegeProbe message)
    {
        lock (Gate) Received.Add(message.Id);
    }
}
