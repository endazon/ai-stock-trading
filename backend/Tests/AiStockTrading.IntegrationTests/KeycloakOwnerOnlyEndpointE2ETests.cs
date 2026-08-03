extern alias RiskManagementWorker;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// issue #82 Slice B / IADR-0050, FR-10/ADR-0007: OwnerOnly 同期照会エンドポイントを実 Keycloak トークンで検証する。
// リスク管理 Worker を実 Keycloak＋実 PostgreSQL＋実 RabbitMQ へ結線し、dev realm の ROPC で取得した
// trading-owner トークンで GET /risk-controls/open-positions（#76 最優先）を叩く。トークンあり→200・なし→401。
// 実 OIDC 発見・JWKS 検証・realm ロール→OwnerOnly ポリシーを通しで確認する（呼び出し側のトークン伝播＝#76 は対象外）。
[Trait("Category", "Integration")]
public sealed class KeycloakOwnerOnlyEndpointE2ETests : IAsyncLifetime
{
    private const string Realm = "ai-stock-trading";

    // 外部インフラ注入時（Docker API が無い環境・E2EInfrastructure 参照）はコンテナを起動しない。
    private readonly PostgreSqlContainer? _postgres = E2EInfrastructure.UseExternal
        ? null
        : new PostgreSqlBuilder("postgres:16").Build();

    private readonly RabbitMqContainer? _rabbitMq = E2EInfrastructure.UseExternal
        ? null
        : new RabbitMqBuilder("rabbitmq:3.13-management").Build();
    private IContainer? _keycloak;
    private WebApplicationFactory<RiskManagementWorker::Program>? _factory;
    private string _keycloakBaseUrl = "";

    public async Task InitializeAsync()
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
        // dev realm（trading-owner ロール・dev-owner ユーザー・direct access grants 有効な public クライアント）を
        // import 起動する。realm-export.json は出力へ複製済み（csproj Content）。KeycloakBuilder は realm import 用の
        // API を持たないため汎用 ContainerBuilder で --import-realm＋リソースマッピングを用いる（IADR-0050 決定3）。
        if (!E2EInfrastructure.UseExternalKeycloak)
        {
            var realmPath = Path.Combine(AppContext.BaseDirectory, "realm-export.json");
            _keycloak = new ContainerBuilder("quay.io/keycloak/keycloak:26.0")
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", "admin")
                .WithResourceMapping(new FileInfo(realmPath), "/opt/keycloak/data/import/")
                .WithCommand("start-dev", "--import-realm")
                .WithPortBinding(8080, true)
                // realm ルートが 200 を返す＝サーバ起動済み＆realm import 完了、を readiness とする。
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

        // トークンの iss と Worker の Auth:Authority を同一 base URL に揃え、JWT 検証（issuer 一致）を成立させる。
        _keycloakBaseUrl = E2EInfrastructure.KeycloakBaseUrl
            ?? $"http://{_keycloak!.Hostname}:{_keycloak.GetMappedPublicPort(8080)}";

        // Worker の Program は接続情報を CreateBuilder 時点で読むため、環境変数（__ 区切り）で注入する（IADR-0049）。
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString());
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString",
            E2EInfrastructure.RabbitMqConnection ?? _rabbitMq!.GetConnectionString());
        Environment.SetEnvironmentVariable("Otlp__Endpoint", "http://localhost:4317");
        Environment.SetEnvironmentVariable("Auth__Authority", $"{_keycloakBaseUrl}/realms/{Realm}");

        _factory = new WebApplicationFactory<RiskManagementWorker::Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("IntegrationTest"));
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
        if (_keycloak is not null)
            disposals.Add(_keycloak.DisposeAsync().AsTask());
        await Task.WhenAll(disposals);

        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
        Environment.SetEnvironmentVariable("RabbitMq__ConnectionString", null);
        Environment.SetEnvironmentVariable("Otlp__Endpoint", null);
        Environment.SetEnvironmentVariable("Auth__Authority", null);
    }

    [Fact]
    public async Task OwnerOnly保有照会は_無トークンで401_trading_ownerトークンで200()
    {
        var factory = _factory!;
        using var client = factory.CreateClient();

        // 未認証 → 401（OwnerOnly ポリシー）。
        var anonymous = await client.GetAsync("/risk-controls/open-positions");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "OwnerOnly は未認証を 401 で弾く");

        // 実 Keycloak の ROPC で dev-owner（trading-owner ロール）のアクセストークンを取得する。
        var token = await GetOwnerAccessTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var authorized = await client.GetAsync("/risk-controls/open-positions");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK,
            "trading-owner トークンで実 OIDC/JWT 検証と OwnerOnly を通過し 200");
    }

    private async Task<string> GetOwnerAccessTokenAsync()
    {
        using var http = new HttpClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "ai-stock-trading-dev",
            ["username"] = "dev-owner",
            ["password"] = "owner",
            ["scope"] = "openid",
        });
        var response = await http.PostAsync(
            $"{_keycloakBaseUrl}/realms/{Realm}/protocol/openid-connect/token", form);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"token endpoint {(int)response.StatusCode}: {body}");
        }
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return payload!.AccessToken;
    }

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken);
}
