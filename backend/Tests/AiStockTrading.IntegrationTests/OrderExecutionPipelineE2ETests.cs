using OrderExecutionService.Application.Ports;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// issue #82 / IADR-0049, FR-05/UC-01: 発注執行パイプラインの実基盤 E2E。
// 実 PostgreSQL・実 RabbitMQ（Testcontainers）を起動し、発注執行 Worker を WebApplicationFactory で
// in-process 起動して実基盤へ結線する。OrderApproved を実キューへ発行 → 購読 → ペーパー執行
//（実弾を撃たない・IADR-0016） → 実 Postgres へ永続 → OrderExecuted 発行、までを検証する。
//
// ADR-0013 / IADR-0129 / #354（第 3 段階）: メッセージング基盤を MassTransit から Wolverine へ移行したことに
// 追随した。発行は `IBus.Publish`（singleton）から **scope から解決した `IMessageBus.PublishAsync`** へ
// （Wolverine の `IMessageBus` は scoped）。併せて、**キュー名がサービス名を前置した名前で実際に宣言され、
// DLQ が `<queue>_error` になっていること**をブローカ上で確かめる（IADR-0129 決定 1・5 の実配線検証）。
//
// [Trait("Category","Integration")]: 既定 CI では --filter Category!=Integration で実行除外し、
// 実基盤 E2E は専用ワークフロー（integration.yml・nightly/dispatch）で実走する（Docker 必須）。
[Trait("Category", "Integration")]
public sealed class OrderExecutionPipelineE2ETests : IAsyncLifetime
{
    // IADR-0129 決定 1: キュー名は <ServiceName>.<メッセージ型名>。ServiceName は Program.cs の定数と同一。
    private const string ExecutionServiceName = "ai-stock-trading.order-execution-service";

    // 外部インフラ注入時（Docker API が無い環境・E2EInfrastructure 参照）はコンテナを起動しない。
    private readonly PostgreSqlContainer? _postgres = E2EInfrastructure.UseExternal
        ? null
        : new PostgreSqlBuilder("postgres:16").Build();

    private readonly RabbitMqContainer? _rabbitMq = E2EInfrastructure.UseExternal
        ? null
        : new RabbitMqBuilder("rabbitmq:3.13-management").Build();

    private WebApplicationFactory<Program>? _factory;
    private string _rabbitMqConnection = string.Empty;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        catch
        {
            // IAsyncLifetime は InitializeAsync が例外送出すると DisposeAsync を呼ばない。片方のみ起動できた
            // 場合のコンテナリークを防ぐため、ここで確実に破棄する（claude-review 指摘）。
            await DisposeAsync();
            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        if (_postgres is not null && _rabbitMq is not null)
            await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        // 発注執行 Worker を実 PostgreSQL・実 RabbitMQ へ結線する（InMemory/スタブへ差し替えない）。
        // 注意: Worker の Program は接続文字列・キュー・ブローカを WebApplication.CreateBuilder 時点の
        // builder.Configuration から読む。WebApplicationFactory の ConfigureAppConfiguration は build 後に
        // 適用されるため間に合わない。CreateBuilder が読み込む「環境変数」（`__` 区切り）で注入する。
        _rabbitMqConnection = E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", _rabbitMqConnection);
        // OTLP は到達不能でも起動は継続する（エクスポートはベストエフォート）。ノイズ低減の固定値。
        Environment.SetEnvironmentVariable("Otlp__Endpoint", "http://localhost:4317");
        // 明示的にペーパー（実弾防止・IADR-0016）。
        Environment.SetEnvironmentVariable("Broker__Provider", "paper");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            // env=Testing は WebApplicationFactory 準拠テストが InMemory 化に使うため避け、実基盤結線用の
            // 独自環境名を用いる（appsettings.Development.json のプレースホルダも読み込まない）。
            builder.UseEnvironment("IntegrationTest"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();

        // 外部注入時はコンテナを持たない（破棄は呼び出し側の責務）。
        var disposals = new List<Task>();
        if (_postgres is not null)
            disposals.Add(_postgres.DisposeAsync().AsTask());
        if (_rabbitMq is not null)
            disposals.Add(_rabbitMq.DisposeAsync().AsTask());
        await Task.WhenAll(disposals);

        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", null);
        Environment.SetEnvironmentVariable("Otlp__Endpoint", null);
        Environment.SetEnvironmentVariable("Broker__Provider", null);
    }

    [Fact]
    public async Task 承認注文を実RabbitMQへ発行するとペーパー執行され実Postgresへ永続される()
    {
        var factory = _factory!;

        // CreateClient で in-process ホストを起動する（起動時に実 EF Migration 適用・Wolverine の購読開始）。
        using var client = factory.CreateClient();

        // ready ヘルスチェック（"ready" タグ）は実 Postgres 疎通を含む。ブローカへの接続と購読の開始は
        // 起動直後は未完のことがあるため、healthy を待ったうえでキューに consumer が付くまで待つ
        //（＝発行前に購読準備が整うゲート）。
        var readyDeadline = DateTime.UtcNow.AddSeconds(30);
        var ready = false;
        while (DateTime.UtcNow < readyDeadline)
        {
            var health = await client.GetAsync("/health/ready");
            if (health.IsSuccessStatusCode)
            {
                ready = true;
                break;
            }

            await Task.Delay(500);
        }

        ready.Should().BeTrue("実 Postgres 疎通が healthy になること");

        // IADR-0129 決定 1・5 の実配線検証: 購読キューはサービス名を前置した名前で宣言され、
        // その DLQ は <queue>_error として宣言される（MassTransit 時代の運用手順の連続性）。
        var queueName = WolverineExtensions.QueueNameFor(ExecutionServiceName, typeof(OrderApproved));
        var queue = await RabbitMqTopologyProbe.WaitForQueueAsync(
            _rabbitMqConnection, queueName, minConsumers: 1, TimeSpan.FromSeconds(30));

        queue.Should().NotBeNull($"購読キュー {queueName} が実ブローカへ宣言されること（AutoProvision）");
        queue!.ConsumerCount.Should().BeGreaterThanOrEqualTo(1, "発注執行 Worker が実際に購読していること");

        var deadLetterQueueName = WolverineExtensions.DeadLetterQueueNameFor(queueName);
        (await RabbitMqTopologyProbe.TryDescribeQueueAsync(_rabbitMqConnection, deadLetterQueueName))
            .Should().NotBeNull(
                $"再試行を使い切った失敗の退避先 {deadLetterQueueName} が宣言されること（IADR-0129 決定 5）");

        var decisionId = Guid.NewGuid();
        // FR-10, #331, IADR-0210: Open 注文は損切り価格が必須（無いと見送り＝逆指値なしの建玉を持たない）。
        var intent = new OrderIntent(
            Symbol: "AAPL",
            Market: Market.UnitedStates,
            Side: TradeSide.Buy,
            ProductType: ProductType.Cash,
            Mode: BrokerProvider.InternalPaper,
            Quantity: 10,
            Price: 150m,
            PositionEffect: PositionEffect.Open,
            StopLossPrice: 140m);
        var approved = new OrderApproved(decisionId, intent, ApprovedQuantity: 10, ApprovedAt: DateTimeOffset.UtcNow);

        // 実 RabbitMQ へ発行する（Worker の実 Wolverine 配線が購読・執行・永続する）。
        await PublishAsync(factory.Services, approved);

        // 実 Postgres の発注結果台帳に DecisionId が永続されるまでポーリングする（購読処理は非同期のため）。
        var record = await WaitForExecutionAsync(factory.Services, decisionId, TimeSpan.FromSeconds(30));

        record.Should().NotBeNull("OrderApproved の購読→ペーパー執行→実 Postgres 永続が成立すること");
    }

    // Wolverine の IMessageBus は **scoped** であるため、必ずスコープから解決して発行する
    //（MassTransit の IBus は singleton だった。移行に伴う解決方法の違い・IADR-0129）。
    internal static async Task PublishAsync(IServiceProvider services, object message)
    {
        using var scope = services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(message);
    }

    internal static async Task<ExecutionRecord?> WaitForExecutionAsync(
        IServiceProvider executionServices, Guid decisionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = executionServices.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IExecutedOrderStore>();
                var record = store.FindByDecisionId(decisionId);
                if (record is not null)
                    return record;
            }

            await Task.Delay(250);
        }

        return null;
    }
}
