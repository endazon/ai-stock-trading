extern alias RiskManagementWorker;
using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// issue #82 Slice C / IADR-0050, FR-10/UC-01: 複数サービスを跨ぐイベント駆動パイプラインの実基盤 E2E。
// リスク管理＋発注執行の 2 Worker を共有 RabbitMQ／共有 PostgreSQL（テーブル非衝突）へ結線し、
// TradeDecisionMade を発行 → リスク管理スクリーニング（実台帳・既定資金 InitialCapital）→ OrderApproved →
// 発注執行（ペーパー・実弾なし・IADR-0016）→ 実 Postgres の executed_orders へ永続、を通しで検証する。
// 同期照会を経由しないため #76（s2s 認証）に依存しない。
[Trait("Category", "Integration")]
public sealed class TradeExecutionPipelineE2ETests : IAsyncLifetime
{
    // 外部インフラ注入時（Docker API が無い環境・E2EInfrastructure 参照）はコンテナを起動しない。
    private readonly PostgreSqlContainer? _postgres = E2EInfrastructure.UseExternal
        ? null
        : new PostgreSqlBuilder("postgres:16").Build();

    private readonly RabbitMqContainer? _rabbitMq = E2EInfrastructure.UseExternal
        ? null
        : new RabbitMqBuilder("rabbitmq:3.13-management").Build();

    // 発注執行の Program は global（無名参照）、リスク管理は extern alias（IADR-0050 決定1）。
    private WebApplicationFactory<Program>? _executionFactory;
    private WebApplicationFactory<RiskManagementWorker::Program>? _riskFactory;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await InitializeCoreAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task InitializeCoreAsync()
    {
        if (_postgres is not null && _rabbitMq is not null)
            await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        // 両 Worker で同一の接続文字列/キューを共有する。テーブルは非衝突（発注執行=executed_orders・
        // リスク管理=台帳/設定ほか）で、EF の __EFMigrationsHistory は MigrationId が異なるため共存する
        // （IADR-0050 決定2）。プロセスグローバルな環境変数のサービス別競合を回避できる。
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString",
            E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString());
        Environment.SetEnvironmentVariable("Otlp__Endpoint", "http://localhost:4317");
        Environment.SetEnvironmentVariable("Broker__Provider", "paper");

        _riskFactory = new WebApplicationFactory<RiskManagementWorker::Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("IntegrationTest"));
        _executionFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("IntegrationTest"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_riskFactory is not null)
            await _riskFactory.DisposeAsync();
        if (_executionFactory is not null)
            await _executionFactory.DisposeAsync();

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
    public async Task 取引判断が承認され発注執行まで複数サービスを跨いで流れる()
    {
        // 両 Worker を起動する（実 EF Migration・実 MassTransit 購読開始）。ready で購読準備完了を待つ。
        using var riskClient = _riskFactory!.CreateClient();
        using var execClient = _executionFactory!.CreateClient();
        await WaitReadyAsync(riskClient);
        await WaitReadyAsync(execClient);

        // 既定資金（InitialCapital=10万）に対し十分小さい新規建て。リスク管理の決定的スクリーニングを通過する。
        var decisionId = Guid.NewGuid();
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, TradeMode.Paper, 10, 1_000m);
        var decision = new TradeDecisionMade(decisionId, intent, "E2E", DateTimeOffset.UtcNow);

        // 取引判断イベントを実 RabbitMQ へ発行する（リスク管理が購読・承認し、発注執行が執行・永続する）。
        var bus = _riskFactory.Services.GetRequiredService<IBus>();
        await bus.Publish(decision);

        // 発注執行の実 Postgres 台帳に DecisionId が現れるまで待つ（RM 承認→OE ペーパー執行→永続の連鎖）。
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ExecutionRecord? record = null;
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _executionFactory.Services.CreateScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IExecutedOrderStore>();
                record = store.FindByDecisionId(decisionId);
            }

            if (record is not null)
                break;

            await Task.Delay(250);
        }

        record.Should().NotBeNull(
            "TradeDecisionMade→リスク管理承認→ペーパー執行→実 Postgres 永続が複数サービス跨ぎで成立すること");
        // 承認された注文の内容が意図どおり連鎖したことを確認する（スクリーニングが元 intent を承認して執行された）。
        record!.Symbol.Should().Be("AAPL");
        record.Side.Should().Be(TradeSide.Buy);
        record.Quantity.Should().Be(10);
    }

    private static async Task WaitReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync("/health/ready");
            if (response.IsSuccessStatusCode)
                return;

            await Task.Delay(500);
        }

        throw new TimeoutException("サービスが ready になりませんでした。");
    }
}
