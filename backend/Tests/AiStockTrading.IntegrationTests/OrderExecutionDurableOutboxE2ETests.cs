using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OrderExecutionService.Infrastructure.Persistence;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 NFR, FR-05, FR-10, #1442（親 #1423 M-4・試験の穴 5）, IADR-0573 決定1: 発注執行の送信（耐久アウトボックス）は、
// **RabbitMQ が落ちている間に出した OrderExecuted を、ブローカーの復旧とプロセスの再起動を挟んでも届ける。**
//
// 既定の RabbitMQ 送信（Inline）は断の間も PublishAsync を成功で返し、3 回の再試行で黙って捨てる（実測。IADR-0573）。
// 本試験は Program.cs と同じ構成（共通の RabbitMQ 配線 ＋ UseOrderExecutionDurableOutbox）で、
//   1. ブローカーを止める → 2. 発行（成功で返る・封筒は PostgreSQL に残る）→ 3. プロセスを畳む →
//   4. ブローカーを起こす → 5. 同じ DB で新しいプロセスを起こす → 6. 封筒が送られ、購読キューに 1 件届き、保存先から消える →
//   7. もう一度起こしても送り直さない
// を実コンテナで確かめる。購読側は fan-out exchange に結んだ素のキューで数える（受け手のサービスを起こさない）。
//
// ブローカーの停止・起動を試験が操作するため、外部注入（E2E_*）の経路では走らない（共有のブローカーを止められない）。
[Trait("Category", "Integration")]
public sealed class OrderExecutionDurableOutboxE2ETests : IAsyncLifetime
{
    private const string ServiceName = "ai-stock-trading.order-execution-service";

    // Wolverine の送信先 exchange 名はメッセージ型の完全名（IADR-0129 決定 2・決定 7）。名前の誤りは手順 0 の到達で赤になる。
    private static readonly string Exchange = typeof(OrderExecuted).FullName!;
    private const string ProbeQueue = "e2e.durable-outbox.order-executed-probe";

    private readonly PostgreSqlContainer _postgres;
    private readonly RabbitMqContainer _rabbitMq;

    public OrderExecutionDurableOutboxE2ETests()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.RabbitMq);
        Assert.SkipWhen(E2EInfrastructure.UseExternal,
            "ブローカーの停止・起動を試験が操作するため、外部注入（E2E_*）の経路では走らない。Testcontainers の経路で走らせること。");

        _postgres = new PostgreSqlBuilder("postgres:16").Build();
        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management").Build();
    }

    public async ValueTask InitializeAsync() =>
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

    public async ValueTask DisposeAsync() =>
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());

    private Task<IHost> StartSenderAsync() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAiStockTradingRabbitMq(ServiceName, _rabbitMq.GetConnectionString());
                opts.UseOrderExecutionDurableOutbox(_postgres.GetConnectionString());
            })
            .StartAsync();

    private static OrderExecuted Execution(string orderId) =>
        new(Guid.NewGuid(), orderId, OrderStatus.Filled, 100, 341m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate);

    private async Task DeclareProbeAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(_rabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        // Wolverine の AutoProvision と同じ宣言（fanout・durable・自動削除しない）。どちらが先に宣言しても衝突しない。
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Fanout, durable: true, autoDelete: false);
        await channel.QueueDeclareAsync(ProbeQueue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(ProbeQueue, Exchange, routingKey: string.Empty);
    }

    private async Task<uint> WaitForProbeCountAsync(uint expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        uint last = 0;
        while (DateTime.UtcNow < deadline)
        {
            last = (await RabbitMqTopologyProbe.TryDescribeQueueAsync(_rabbitMq.GetConnectionString(), ProbeQueue))?.MessageCount ?? 0;
            if (last >= expected)
                return last;
            await Task.Delay(500);
        }

        return last;
    }

    private async Task<long> CountStoredOutgoingAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"select count(*) from {WolverineDurableOutbox.Schema}.wolverine_outgoing_envelopes", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> WaitForStoredOutgoingAsync(long expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long last;
        do
        {
            last = await CountStoredOutgoingAsync();
            if (last == expected)
                return last;
            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);

        return last;
    }

    [Fact]
    public async Task ブローカー断の間に発行した約定はブローカーの復旧とプロセスの再起動の後に届く()
    {
        await DeclareProbeAsync();

        // 0. ブローカーが生きている間の発行は届く（exchange 名と配線の確認）。
        var sender = await StartSenderAsync();
        await new MessageBus(sender.Services.GetRequiredService<IWolverineRuntime>()).PublishAsync(Execution("warm-up"));
        (await WaitForProbeCountAsync(1, TimeSpan.FromSeconds(30))).Should().Be(1);

        // 1〜2. ブローカーを止めて発行する。発行は成功で返り、封筒は PostgreSQL に残る。
        await _rabbitMq.StopAsync();
        await new MessageBus(sender.Services.GetRequiredService<IWolverineRuntime>()).PublishAsync(Execution("during-outage"));
        (await CountStoredOutgoingAsync()).Should().Be(1, "断の間の封筒は送れずに保存先へ残る（既定の送信ではここで黙って捨てられた）");

        // 3. プロセスを畳む（配備の入れ替え＝SIGTERM と同じ停止。停止で封筒は「持ち主なし」へ戻る）。
        //    🔴 停止せずに破棄だけすると、同じテストプロセスの中に旧ホストの送信の代理が生き残り、ブローカーの復旧後に
        //    それが送って封筒を消せない（実測。本物のプロセスの落下ではこの残骸は無い——落下〔FailFast〕も別に実測した。IADR-0573）。
        await StopQuietlyAsync(sender);
        await E2EInfrastructure.DisposeQuietlyAsync(sender as IAsyncDisposable, "断の間の発注執行のホスト");

        // 4〜5. ブローカーを起こし（durable なキューと結びは残る）、同じ DB で新しいプロセスを起こす。
        await _rabbitMq.StartAsync();
        var restarted = await StartSenderAsync();
        try
        {
            // 6. 単独ノード（Solo）は起動の直後に残った封筒を送る。購読キューには warm-up と合わせて 2 件。
            (await WaitForProbeCountAsync(2, TimeSpan.FromSeconds(60))).Should().Be(2,
                "断の間に出した約定は、復旧と再起動の後に 1 回だけ届く");
            (await WaitForStoredOutgoingAsync(0, TimeSpan.FromSeconds(30))).Should().Be(0,
                "送り終えた封筒は保存先から消える（回収の経路が送信先を Inline で作ると、送っても消えずに再起動のたびに送り直す——実測）");

            // 回収の経路が作った送信先も耐久のまま（Inline に落ちると、以後このプロセスの OrderExecuted は保存されない——実測）。
            var exchange = new Uri($"rabbitmq://exchange/{Exchange}");
            restarted.Services.GetRequiredService<IWolverineRuntime>().Options.Transports.AllEndpoints()
                .Single(e => e.Uri == exchange).Mode.Should().Be(EndpointMode.Durable);
        }
        finally
        {
            await StopQuietlyAsync(restarted);
            await E2EInfrastructure.DisposeQuietlyAsync(restarted as IAsyncDisposable, "再起動した発注執行のホスト");
        }

        // 7. もう一度起こしても送り直さない（封筒は消えている）。
        var again = await StartSenderAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            (await WaitForProbeCountAsync(3, TimeSpan.FromSeconds(5))).Should().Be(2, "送り終えた約定を再起動のたびに送り直さない");
        }
        finally
        {
            await StopQuietlyAsync(again);
            await E2EInfrastructure.DisposeQuietlyAsync(again as IAsyncDisposable, "もう一度起こした発注執行のホスト");
        }
    }

    // 配備の入れ替え（SIGTERM）と同じ停止。ブローカーが居ないと閉じ待ちが打ち切られ得る（判定には影響しない）。
    private static async Task StopQuietlyAsync(IHost host)
    {
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await host.StopAsync(stopping.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
