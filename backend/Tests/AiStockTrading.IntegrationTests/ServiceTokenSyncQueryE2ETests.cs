extern alias RiskManagementWorker;
extern alias ReportWorker;
extern alias CostControlWorker;
using System.Net;
using System.Net.Http.Json;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// issue #82 Slice D / IADR-0051, #76: s2s トークン伝播つき同期照会の実コンテナ E2E。
// 呼び出し側の実コンポーネント（ClientCredentialsTokenProvider＋ServiceTokenHandler・#117）が実 Keycloak の
// confidential クライアント（ai-stock-trading-svc）から client_credentials で trading-service トークンを取得し、
// 提供側 Worker（リスク管理・報告書）の読み取り系同期照会を認証済みで通せることを検証する。
// - 無トークン→401 / trading-service→読み取り系は通過（open-positions/sizing-context=200・daily-policy=空DBで404）
// - 書き込み系（OwnerOnly）を trading-service で呼ぶと 403（最小権限の非回帰）
// これにより #76 受け入れ条件4（実コンテナ疎通）も満たす。
[Trait("Category", "Integration")]
public sealed class ServiceTokenSyncQueryE2ETests : IAsyncLifetime
{
    private const string Realm = "ai-stock-trading";

    private readonly PostgreSqlContainer? _postgres;
    private readonly RabbitMqContainer? _rabbitMq;

    private IContainer? _keycloak;
    private WebApplicationFactory<RiskManagementWorker::Program>? _riskFactory;
    private WebApplicationFactory<ReportWorker::Program>? _reportFactory;
    private WebApplicationFactory<CostControlWorker::Program>? _costFactory;
    private HttpClient? _tokenHttp;
    private string _tokenEndpoint = "";

    public ServiceTokenSyncQueryE2ETests()
    {
        // NFR, MSP/ADR-0090 決定 1・2, IADR-0497 (#1200): 要る依存を得られなければ、コンテナを組み立てる前に理由つきで skip する
        // （Docker に届かない環境では `Build()` 自体が投げるため、門はフィールド初期化子より前＝ここに置く）。
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres, RequiredServices.RabbitMq, RequiredServices.Keycloak);

        // 外部インフラ注入時（E2E_*。E2EInfrastructure 参照）はコンテナを起動しない。
        _postgres = E2EInfrastructure.UseExternal ? null : new PostgreSqlBuilder("postgres:16").Build();
        _rabbitMq = E2EInfrastructure.UseExternal ? null : new RabbitMqBuilder("rabbitmq:3.13-management").Build();
    }

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
        // dev realm には trading-service ロールと confidential クライアント ai-stock-trading-svc（client_credentials）が
        // 含まれる（infra/keycloak/realm-export.json・#117）。
        if (!E2EInfrastructure.UseExternalKeycloak)
        {
            var realmPath = Path.Combine(AppContext.BaseDirectory, "realm-export.json");
            _keycloak = new ContainerBuilder("quay.io/keycloak/keycloak:26.0")
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
                .WithResourceMapping(new FileInfo(realmPath), "/opt/keycloak/data/import/")
                .WithCommand("start-dev", "--import-realm")
                .WithPortBinding(8080, true)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPath($"/realms/{Realm}").ForPort(8080)))
                .Build();
        }

        var startups = new List<Task>();
        if (_postgres is not null)
            startups.Add(_postgres.StartAsync());
        if (_rabbitMq is not null)
            startups.Add(_rabbitMq.StartAsync());
        if (_keycloak is not null)
            startups.Add(_keycloak.StartAsync());
        await Task.WhenAll(startups);

        var keycloakBase = E2EInfrastructure.KeycloakBaseUrl
            ?? $"http://{_keycloak!.Hostname}:{_keycloak.GetMappedPublicPort(8080)}";
        _tokenEndpoint = $"{keycloakBase}/realms/{Realm}/protocol/openid-connect/token";

        // 提供側 Worker は同一 PostgreSQL を共有（テーブル非衝突・IADR-0050 決定2）。Auth:Authority を実 Keycloak に向ける。
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString",
            E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString());
        Environment.SetEnvironmentVariable("Otlp__Endpoint", "http://localhost:4317");
        Environment.SetEnvironmentVariable("Auth__Authority", $"{keycloakBase}/realms/{Realm}");

        _riskFactory = new WebApplicationFactory<RiskManagementWorker::Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("IntegrationTest"));
        _reportFactory = new WebApplicationFactory<ReportWorker::Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("IntegrationTest"));
        // #79, IADR-0031/0051: poller が s2s で照会する /costs/state の提供側。
        _costFactory = new WebApplicationFactory<CostControlWorker::Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("IntegrationTest"));
        _tokenHttp = new HttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        _tokenHttp?.Dispose();

        // NFR, #1128: ホストの破棄の打ち切り（RabbitMQ の閉じ待ち）で試験を赤にせず、1 つが投げても残りの破棄を必ず行う。
        try
        {
            try
            {
                try
                {
                    await E2EInfrastructure.DisposeQuietlyAsync(_riskFactory, "リスク管理のホスト");
                }
                finally
                {
                    await E2EInfrastructure.DisposeQuietlyAsync(_reportFactory, "報告書のホスト");
                }
            }
            finally
            {
                await E2EInfrastructure.DisposeQuietlyAsync(_costFactory, "費用統制のホスト");
            }
        }
        finally
        {
            await DisposeInfrastructureAsync();
        }
    }

    private async Task DisposeInfrastructureAsync()
    {
        // 外部注入時はコンテナを持たない（破棄は呼び出し側の責務）。
        var disposals = new List<Task>();
        if (_postgres is not null)
            disposals.Add(_postgres.DisposeAsync().AsTask());
        if (_rabbitMq is not null)
            disposals.Add(_rabbitMq.DisposeAsync().AsTask());
        if (_keycloak is not null)
            disposals.Add(_keycloak.DisposeAsync().AsTask());
        try
        {
            await Task.WhenAll(disposals);
        }
        finally
        {
            // NFR, #1128: コンテナの破棄が投げても、環境変数は後続の試験クラスへ漏らさない。
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", null);
            Environment.SetEnvironmentVariable("Otlp__Endpoint", null);
            Environment.SetEnvironmentVariable("Auth__Authority", null);
        }
    }

    [Fact]
    public async Task サービストークンで読み取り系同期照会は通過_無トークンは401_書き込みは403()
    {
        // 呼び出し側の実コンポーネント（#117）で trading-service トークンを取得・付与する。トークン取得は実 Keycloak へ
        // 実 HTTP、API 呼び出しは in-process トランスポート（CreateDefaultClient にハンドラを差し込む）。
        var options = new ServiceAuthOptions
        {
            TokenEndpoint = _tokenEndpoint,
            ClientId = "ai-stock-trading-svc",
            ClientSecret = "dev-only-service-secret",
        };
        var tokenProvider = new ClientCredentialsTokenProvider(
            _tokenHttp!, options, NullLogger<ClientCredentialsTokenProvider>.Instance, TimeProvider.System);

        var riskAnon = _riskFactory!.CreateClient();
        var riskService = _riskFactory.CreateDefaultClient(new ServiceTokenHandler(tokenProvider));
        var reportAnon = _reportFactory!.CreateClient();
        var reportService = _reportFactory.CreateDefaultClient(new ServiceTokenHandler(tokenProvider));
        var costAnon = _costFactory!.CreateClient();
        var costService = _costFactory.CreateDefaultClient(new ServiceTokenHandler(tokenProvider));

        // 提供側の起動（実 EF Migration・Wolverine 購読）を待つ。
        await WaitReadyAsync(riskAnon);
        await WaitReadyAsync(reportAnon);
        await WaitReadyAsync(costAnon);

        // 無トークン → 401（OwnerOrService も未認証は弾く）。
        (await riskAnon.GetAsync("/risk-controls/open-positions")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "未認証の同期照会は 401");
        (await reportAnon.GetAsync("/reports/daily-policy")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "未認証の同期照会は 401");
        (await costAnon.GetAsync("/costs/state")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "未認証の同期照会は 401");

        // trading-service トークン → 読み取り系を通過。open-positions/sizing-context はデータ有無に依らず 200。
        (await riskService.GetAsync("/risk-controls/open-positions")).StatusCode
            .Should().Be(HttpStatusCode.OK, "trading-service で open-positions を通過");
        (await riskService.GetAsync("/risk-controls/sizing-context")).StatusCode
            .Should().Be(HttpStatusCode.OK, "trading-service で sizing-context を通過");
        // daily-policy は空 DB（未確定）で 404。401 ではなく 404 になること自体が、認可を通過して
        // ハンドラへ到達したこと（trading-service が OwnerOrService を満たすこと）を示す。
        (await reportService.GetAsync("/reports/daily-policy")).StatusCode
            .Should().Be(HttpStatusCode.NotFound,
                "trading-service で daily-policy の認可を通過（空 DB のためデータは 404）");

        // #79, IADR-0031: 定時サイクル poller が費用統制の状態を照会できること（実 Keycloak トークンで OwnerOrService を通過）。
        (await costService.GetAsync("/costs/state")).StatusCode
            .Should().Be(HttpStatusCode.OK, "trading-service で costs/state を通過（poller の間隔延長/停止の入力）");
        (await costService.GetAsync("/costs/review?capital=1000000")).StatusCode
            .Should().Be(HttpStatusCode.OK, "trading-service で costs/review を通過");

        // 非回帰（最小権限）: 書き込み系（OwnerOnly）を trading-service で呼ぶと 403。認可は本体実行前に評価される
        // ため、リクエストボディの妥当性に依らず 403 になる。
        var write = await riskService.PostAsJsonAsync(
            "/risk-controls/kill-switch/engage", new { reason = "e2e" });
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "サービスロール（trading-service）は書き込み系（OwnerOnly）を実行できない");

        // 費用計上（OwnerOnly）も trading-service では 403（サービスからの計上はイベント経由・IADR-0055 決定1）。
        var costWrite = await costService.PostAsJsonAsync(
            "/costs/record", new { Category = "Llm", Amount = 1m });
        costWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "サービスロールは費用の書き込み（/costs/record）を実行できない＝最小権限");
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
