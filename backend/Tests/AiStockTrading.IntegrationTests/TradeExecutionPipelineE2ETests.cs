extern alias RiskManagementWorker;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;
using RiskManagementDbContext = RiskManagementWorker::AiStockTrading.RiskManagement.Infrastructure.Foundation.Persistence.RiskManagementDbContext;

namespace AiStockTrading.IntegrationTests;

// issue #82 Slice C / IADR-0050, FR-10/UC-01: 複数サービスを跨ぐイベント駆動パイプラインの実基盤 E2E。
// リスク管理＋発注執行の 2 Worker を共有 RabbitMQ／共有 PostgreSQL（テーブル非衝突）へ結線し、
// TradeDecisionMade を発行 → リスク管理スクリーニング（実台帳・既定資金 InitialCapital＝$3,000）→ OrderApproved →
// 発注執行（ペーパー・実弾なし・IADR-0016）→ 実 Postgres の executed_orders へ永続、を通しで検証する。
// 同期照会を経由しないため #76（s2s 認証）に依存しない。
//
// ADR-0013 / IADR-0129 / #354（第 3 段階）: Wolverine 移行に追随した。発行は scope から解決した
// `IMessageBus.PublishAsync`（Wolverine の IMessageBus は scoped）。**本クラスは #354 の受け入れ基準
// 「pub/sub の fan-out が competing consumer へ退行していないこと」を実ブローカで検証する場所**でもある
//（下の fan_out テスト）。2 Worker が同居する唯一の fixture であり、退行を実配線で観測できる。
[Trait("Category", "Integration")]
public sealed class TradeExecutionPipelineE2ETests : IAsyncLifetime
{
    // IADR-0129 決定 1: キュー名は <ServiceName>.<メッセージ型名>。各 Program.cs の ServiceName 定数と同一。
    private const string RiskServiceName = "ai-stock-trading.risk-management-service";
    private const string ExecutionServiceName = "ai-stock-trading.order-execution-service";

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
    private string _rabbitMqConnection = string.Empty;

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

        // 両 Worker で同一の接続文字列/ブローカを共有する。テーブルは非衝突（発注執行=executed_orders・
        // リスク管理=台帳/設定ほか）で、EF の __EFMigrationsHistory は MigrationId が異なるため共存する
        // （IADR-0050 決定2）。プロセスグローバルな環境変数のサービス別競合を回避できる。
        _rabbitMqConnection = E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString();
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", _rabbitMqConnection);
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
        // 両 Worker を起動する（実 EF Migration・実 Wolverine 購読開始）。ready で DB 疎通を待ち、
        // そのあとキューに consumer が付くまで待って購読準備完了とする。
        using var riskClient = _riskFactory!.CreateClient();
        using var execClient = _executionFactory!.CreateClient();
        await WaitReadyAsync(riskClient);
        await WaitReadyAsync(execClient);
        await WaitSubscribedAsync(RiskServiceName, typeof(TradeDecisionMade));

        // #364, IADR-0152 決定1/3: 既定資金（InitialCapital ＝ equity $3,000・基準通貨 USD）に対し十分小さい
        // 新規建て。10 株 × $20 ＝ $200 は 1 注文上限（25% ＝ $750）・日次上限（150% ＝ $4,500）の内側であり、
        // リスク管理の決定的スクリーニングを通過する。AAPL は基準通貨市場のため換算レートは既定 1 でよい。
        var decisionId = Guid.NewGuid();
        // FR-10, #331: Open 注文は損切り価格が必須（発注執行が保護逆指値を同時発注する）。
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 10, 20m,
            StopLossPrice: 18m);
        var decision = new TradeDecisionMade(decisionId, intent, "E2E", DateTimeOffset.UtcNow);

        // 取引判断イベントを実 RabbitMQ へ発行する（リスク管理が購読・承認し、発注執行が執行・永続する）。
        await OrderExecutionPipelineE2ETests.PublishAsync(_riskFactory.Services, decision);

        // 発注執行の実 Postgres 台帳に DecisionId が現れるまで待つ（RM 承認→OE ペーパー執行→永続の連鎖）。
        var record = await OrderExecutionPipelineE2ETests.WaitForExecutionAsync(
            _executionFactory.Services, decisionId, TimeSpan.FromSeconds(30));

        record.Should().NotBeNull(
            "TradeDecisionMade→リスク管理承認→ペーパー執行→実 Postgres 永続が複数サービス跨ぎで成立すること");
        // 承認された注文の内容が意図どおり連鎖したことを確認する（スクリーニングが元 intent を承認して執行された）。
        record!.Symbol.Should().Be("AAPL");
        record.Side.Should().Be(TradeSide.Buy);
        record.Quantity.Should().Be(10);
    }

    // NFR, ADR-0013, IADR-0129 決定 1・3, #258 / #354 の受け入れ基準:
    // **1 通のイベントが購読する全サービスへそれぞれ届くこと（fan-out）を実ブローカで確かめる。**
    //
    // 退行の形は 2 つあり、どちらもビルド・ユニットテストが緑のまま起こる:
    //   A. competing consumer: 複数サービスが 1 本のキューを共有し、ブローカが round-robin で配る
    //      （#258 の事故。片方しか受け取らない）。Wolverine の既定キュー名はメッセージ型だけから導かれるため、
    //      決定 1（サービス名の前置）が効いていなければ**必ず**この形になる。
    //   B. ローカル閉じ込め: 発行元プロセスにハンドラがあると発行がプロセス内に閉じる（決定 3 が無い場合）。
    //      OrderApproved はまさにリスク管理が**発行しつつ購読する**型であり、この退行の直撃対象である。
    //
    // 検証の設計: OrderApproved を 1 通だけ発行し、
    //   ① 実ブローカ上で購読キューが**サービスごとに別々に**宣言され、各々に consumer が付いていること
    //      （受動宣言。1 本を取り合っていれば、この 2 本のうち一方は存在しない）
    //   ② その 1 通で**発注執行の執行結果**と**リスク管理の取引台帳**の**両方**が動くこと
    //      （退行 A なら round-robin でどちらか一方しか動かず、退行 B なら発注執行が一切動かない）
    // を確かめる。①だけでは「宣言はされたが配送されない」を、②だけでは「たまたま両方動いた」を排除できない。
    [Fact]
    public async Task 同一イベントは購読する全サービスへ届く_fan_outがcompeting_consumerへ退行していない()
    {
        using var riskClient = _riskFactory!.CreateClient();
        using var execClient = _executionFactory!.CreateClient();
        await WaitReadyAsync(riskClient);
        await WaitReadyAsync(execClient);

        // ① トポロジ: OrderApproved の購読キューはサービスごとに別（決定 1）。
        var execQueueName = WolverineExtensions.QueueNameFor(ExecutionServiceName, typeof(OrderApproved));
        var riskQueueName = WolverineExtensions.QueueNameFor(RiskServiceName, typeof(OrderApproved));
        execQueueName.Should().NotBe(riskQueueName, "同一キューを共有すると pub/sub が取り合いへ退行する");

        var execQueue = await RabbitMqTopologyProbe.WaitForQueueAsync(
            _rabbitMqConnection, execQueueName, minConsumers: 1, TimeSpan.FromSeconds(30));
        var riskQueue = await RabbitMqTopologyProbe.WaitForQueueAsync(
            _rabbitMqConnection, riskQueueName, minConsumers: 1, TimeSpan.FromSeconds(30));

        execQueue.Should().NotBeNull($"発注執行の購読キュー {execQueueName} が実ブローカに存在すること");
        riskQueue.Should().NotBeNull($"リスク管理の購読キュー {riskQueueName} が実ブローカに存在すること");
        execQueue!.ConsumerCount.Should().BeGreaterThanOrEqualTo(1);
        riskQueue!.ConsumerCount.Should().BeGreaterThanOrEqualTo(1);

        // DLQ もキュー単位で分かれて宣言される（IADR-0129 決定 5・運用手順の連続性）。
        (await RabbitMqTopologyProbe.TryDescribeQueueAsync(
                _rabbitMqConnection, WolverineExtensions.DeadLetterQueueNameFor(execQueueName)))
            .Should().NotBeNull("<queue>_error が退避先として宣言されること");
        (await RabbitMqTopologyProbe.TryDescribeQueueAsync(
                _rabbitMqConnection, WolverineExtensions.DeadLetterQueueNameFor(riskQueueName)))
            .Should().NotBeNull("<queue>_error が退避先として宣言されること");

        // ② 実配送: 1 通の OrderApproved で、購読する 2 サービスが**どちらも**処理する。
        var decisionId = Guid.NewGuid();
        var intent = new OrderIntent(
            "MSFT", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper, 5, 200m,
            StopLossPrice: 180m); // FR-10, #331: Open は損切り価格が必須
        var approved = new OrderApproved(decisionId, intent, ApprovedQuantity: 5, ApprovedAt: DateTimeOffset.UtcNow);

        // 発行元はリスク管理（＝この型を自分でも購読しているプロセス。退行 B の直撃対象）。
        await OrderExecutionPipelineE2ETests.PublishAsync(_riskFactory.Services, approved);

        var executed = await OrderExecutionPipelineE2ETests.WaitForExecutionAsync(
            _executionFactory.Services, decisionId, TimeSpan.FromSeconds(30));
        var ledgered = await WaitForRiskLedgerApprovalAsync(decisionId, TimeSpan.FromSeconds(30));

        executed.Should().NotBeNull(
            "発注執行サービスが OrderApproved を受け取って執行すること"
            + "（受け取れないのは、キューを取り合っている＝退行 A か、発行がプロセス内へ閉じている＝退行 B）");
        ledgered.Should().BeTrue(
            "リスク管理サービスも同じ 1 通を受け取って取引台帳へ計上すること"
            + "（片方しか動かないのが competing consumer への退行の形である）");
    }

    // リスク管理の取引台帳（ApprovedOrders・IADR-0018）に承認が計上されたか。
    // 台帳ストア（IPortfolioLedgerStore）に DecisionId 単位の読み出しが無いため、専有 DB を直接読む
    // （PositionDriftStateConcurrencyE2ETests と同じく InternalsVisibleTo 経由）。
    private async Task<bool> WaitForRiskLedgerApprovalAsync(Guid decisionId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _riskFactory!.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<RiskManagementDbContext>();
                if (await db.ApprovedOrders.AsNoTracking().AnyAsync(r => r.DecisionId == decisionId))
                    return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    // 購読準備の待ち合わせ: 対象キューに consumer が付くまで待つ（付かなければ表明で落とす）。
    private async Task WaitSubscribedAsync(string serviceName, Type messageType)
    {
        var queueName = WolverineExtensions.QueueNameFor(serviceName, messageType);
        var queue = await RabbitMqTopologyProbe.WaitForQueueAsync(
            _rabbitMqConnection, queueName, minConsumers: 1, TimeSpan.FromSeconds(30));

        queue.Should().NotBeNull($"購読キュー {queueName} が宣言され consumer が付くこと（発行前のゲート）");
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
