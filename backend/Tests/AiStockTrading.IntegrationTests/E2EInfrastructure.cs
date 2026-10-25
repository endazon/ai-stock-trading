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

    /// <summary>
    /// NFR, #1128: in-process ホスト（<c>WebApplicationFactory</c>）を破棄する。後片付けの中で RabbitMQ の接続を閉じる待ちが
    /// 打ち切られた（<see cref="OperationCanceledException"/>）ときだけ、標準エラーへ残して続ける。
    /// <para>
    /// 試験の判定はホストの破棄より前に済んでいる。打ち切りを赤にすると、判定が通った試験が後片付けの時間切れで落ち、
    /// 後続の破棄（コンテナ・環境変数）にも届かない（2026-09-30 の実測）。ほかの例外は後片付けの本物の不具合なので投げる。
    /// </para>
    /// <para>
    /// 握るのは打ち切り全般（ホストの停止の打ち切りも含む）であり、閉じ待ちに限らない。どの段かは標準エラーのスタックで見る。
    /// 打ち切られた接続が半開きで残り得るが、Testcontainers の経路では直後にブローカのコンテナごと破棄する。
    /// 外部注入（E2E_*）で共有するブローカでは consumer が残り得る（作業仕様書の残余）。
    /// </para>
    /// </summary>
    /// <remarks>
    /// NFR, IADR-0591 決定 2, MSP#2265: 破棄には上限（既定 <see cref="DisposeLimit"/>）を置く。超えたら、どのホストの破棄が
    /// 終わらないかを名指しした <see cref="TimeoutException"/> で**試験を赤にする**（黙って続けない）。
    /// これは根の修正（停止の束ね・IADR-0591 決定 1）の補助で、blame-hang の 5 分の無言の中断より速く、原因を名指して落とす。
    /// </remarks>
    public static async Task DisposeQuietlyAsync(IAsyncDisposable? host, string name, TimeSpan? limit = null)
    {
        if (host is null)
        {
            return;
        }

        var bound = limit ?? DisposeLimit;
        try
        {
            var disposing = host.DisposeAsync().AsTask();
            using var delay = new CancellationTokenSource();
            if (await Task.WhenAny(disposing, Task.Delay(bound, delay.Token)) != disposing)
            {
                throw new TimeoutException(
                    $"[E2E 後片付け] {name} の破棄が {bound.TotalSeconds:0} 秒以内に終わらない。"
                    + "停止の呼び出しが排出の完了を待たずに終わり、排出の途中でコンテナが破棄された疑いがある"
                    + "（MSP#2265・IADR-0591）。テストホストの hang dump で、止まっている非同期の連鎖を見る。");
            }

            await delay.CancelAsync();
            await disposing;
        }
        catch (OperationCanceledException ex)
        {
            await Console.Error.WriteLineAsync(
                $"[E2E 後片付け] {name} の破棄が打ち切られました（試験の判定には影響しない。どの段かはスタックで見る）: {ex}");
        }
    }

    /// <summary>NFR, IADR-0591 決定 2: ホストの破棄の上限（blame-hang の 5 分より短く、平常の破棄〔数秒〕より十分長い）。</summary>
    public static TimeSpan DisposeLimit { get; } = TimeSpan.FromMinutes(2);

    private static string? Value(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
