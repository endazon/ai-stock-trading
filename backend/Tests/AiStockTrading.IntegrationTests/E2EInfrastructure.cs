namespace AiStockTrading.IntegrationTests;

// issue #82 / IADR-0049 の補足: Testcontainers は **Docker API**（npipe/unix socket）を要求するため、
// containerd 系ランタイム（例: Rancher Desktop の containerd/nerdctl 構成）では
// "Failed to connect to Docker endpoint" で実走できない。
//
// そこで「外部で用意した実インフラのエンドポイントを環境変数で注入する」経路を用意する。
// 検証対象が **実 PostgreSQL / 実 RabbitMQ / 実 Keycloak** である点は Testcontainers 経路と同じで、
// 「実基盤 E2E」の意義（InMemory/モックへ差し替えない）は保たれる。差は「コンテナの起動主体」だけ。
//
//   E2E_POSTGRES_CONNECTION  例: Host=localhost;Port=55432;Database=e2e;Username=e2e;Password=e2e
//   E2E_RABBITMQ_CONNECTION  例: amqp://guest:guest@localhost:55672
//   E2E_KEYCLOAK_BASEURL     例: http://localhost:58080
//
// 未設定なら従来どおり Testcontainers がコンテナを起動する（CI の integration.yml は Docker 前提のため無影響）。
// 注意: 外部注入時はインフラの起動・realm import・破棄は呼び出し側（スクリプト）の責務。
internal static class E2EInfrastructure
{
    public static string? PostgresConnection => Value("E2E_POSTGRES_CONNECTION");

    public static string? RabbitMqConnection => Value("E2E_RABBITMQ_CONNECTION");

    public static string? KeycloakBaseUrl => Value("E2E_KEYCLOAK_BASEURL");

    /// <summary>
    /// Postgres/RabbitMQ が外部注入されているか。true なら Testcontainers を起動しない。
    /// <para>
    /// **片方だけの設定は許さない**（fail fast）。fixture はコンテナ起動を `UseExternal` で、接続先を
    /// 各 env の有無で決めるため、片方だけ設定されると「Testcontainers を起動したのに接続先は外部」という
    /// 食い違いが起き、起動していない/無関係な対象を静かに検証してしまう（claude-review 指摘）。
    /// </para>
    /// </summary>
    public static bool UseExternal
    {
        get
        {
            var hasPostgres = PostgresConnection is not null;
            var hasRabbitMq = RabbitMqConnection is not null;
            if (hasPostgres != hasRabbitMq)
            {
                throw new InvalidOperationException(
                    "E2E_POSTGRES_CONNECTION と E2E_RABBITMQ_CONNECTION は両方設定するか両方未設定にしてください"
                    + "（片方だけだと Testcontainers の起動と実際の接続先が食い違い、意図しない対象を検証します）。"
                    + " scripts/e2e-local-infra.sh は 3 変数をまとめて出力します。");
            }
            return hasPostgres && hasRabbitMq;
        }
    }

    /// <summary>Keycloak も含め外部注入されているか（Keycloak を要する E2E 用）。</summary>
    public static bool UseExternalKeycloak => KeycloakBaseUrl is not null;

    private static string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
