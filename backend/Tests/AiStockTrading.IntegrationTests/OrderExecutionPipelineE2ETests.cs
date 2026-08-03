using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// issue #82 / IADR-0049, FR-05/UC-01: 発注執行パイプラインの実基盤 E2E。
// 実 PostgreSQL・実 RabbitMQ（Testcontainers）を起動し、発注執行 Worker を WebApplicationFactory で
// in-process 起動して実基盤へ結線する。OrderApproved を実キューへ発行 → 購読 → ペーパー執行
//（実弾を撃たない・IADR-0016） → 実 Postgres へ永続 → OrderExecuted 発行、までを検証する。
//
// [Trait("Category","Integration")]: 既定 CI では --filter Category!=Integration で実行除外し、
// 実基盤 E2E は専用ワークフロー（integration.yml・nightly/dispatch）で実走する（Docker 必須）。
[Trait("Category", "Integration")]
public sealed class OrderExecutionPipelineE2ETests : IAsyncLifetime
{
    // 外部インフラ注入時（Docker API が無い環境・E2EInfrastructure 参照）はコンテナを起動しない。
    private readonly PostgreSqlContainer? _postgres = E2EInfrastructure.UseExternal
        ? null
        : new PostgreSqlBuilder("postgres:16").Build();

    private readonly RabbitMqContainer? _rabbitMq = E2EInfrastructure.UseExternal
        ? null
        : new RabbitMqBuilder("rabbitmq:3.13-management").Build();

    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
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

        // 発注執行 Worker を実 PostgreSQL・実 RabbitMQ へ結線する（InMemory/テストハーネスへ差し替えない）。
        // 注意: Worker の Program は接続文字列・キュー・ブローカを WebApplication.CreateBuilder 時点の
        // builder.Configuration から読む。WebApplicationFactory の ConfigureAppConfiguration は build 後に
        // 適用されるため間に合わない。CreateBuilder が読み込む「環境変数」（`__` 区切り）で注入する。
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString",
            E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString());
        // OTLP は到達不能でも起動は継続する（エクスポートはベストエフォート）。ノイズ低減の固定値。
        Environment.SetEnvironmentVariable("Otlp__Endpoint", "http://localhost:4317");
        // 明示的にペーパー（実弾防止・IADR-0016）。
        Environment.SetEnvironmentVariable("Broker__Provider", "paper");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            // env=Testing は WebApplicationFactory 準拠テストが InMemory 化に使うため避け、実基盤結線用の
            // 独自環境名を用いる（appsettings.Development.json のプレースホルダも読み込まない）。
            builder.UseEnvironment("IntegrationTest"));
    }

    public async Task DisposeAsync()
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

        // CreateClient で in-process ホストを起動する（起動時に実 EF Migration 適用・MassTransit 購読開始）。
        using var client = factory.CreateClient();

        // ready ヘルスチェック（"ready" タグ）は実 Postgres 疎通と MassTransit バス接続を含む。バス接続は
        // 起動直後は未完のことがあるため、healthy になるまで待つ（＝発行前に購読準備が整うゲート）。
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

        ready.Should().BeTrue("実 Postgres 疎通と MassTransit バス接続が healthy になること");

        var decisionId = Guid.NewGuid();
        var intent = new OrderIntent(
            Symbol: "AAPL",
            Market: Market.UnitedStates,
            Side: TradeSide.Buy,
            ProductType: ProductType.Cash,
            Mode: TradeMode.Paper,
            Quantity: 10,
            Price: 150m,
            PositionEffect: PositionEffect.Open);
        var approved = new OrderApproved(decisionId, intent, ApprovedQuantity: 10, ApprovedAt: DateTimeOffset.UtcNow);

        // 実 RabbitMQ へ発行する（Worker の実 MassTransit 配線が購読・執行・永続する）。
        var bus = factory.Services.GetRequiredService<IBus>();
        await bus.Publish(approved);

        // 実 Postgres の発注結果台帳に DecisionId が永続されるまでポーリングする（購読処理は非同期のため）。
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ExecutionRecord? record = null;
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = factory.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IExecutedOrderStore>();
                record = store.FindByDecisionId(decisionId);
            }

            if (record is not null)
                break;

            await Task.Delay(250);
        }

        record.Should().NotBeNull("OrderApproved の購読→ペーパー執行→実 Postgres 永続が成立すること");
    }
}
