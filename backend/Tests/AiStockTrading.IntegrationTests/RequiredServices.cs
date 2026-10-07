using DotNet.Testcontainers.Configurations;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// NFR, MSP/ADR-0090 決定 1・2（planning#575）, IADR-0497 (#1200):
// **統合テストが「自分の依存を得られるか」を判定する唯一の点。** MSP/IADR-0414 の形（依存ごとに訊く）を写した。
//
// 🔴 従前は門が無く、Docker に届かない環境ではコンテナを組み立てる `Build()` が `DockerUnavailableException` を投げて
// **fail** していた。だが試験が要るのは PostgreSQL・RabbitMQ・Keycloak という**サービス**であり、Docker はその入手経路の
// 1 つでしかない —— 外部供給の口（`E2EInfrastructure` の `E2E_*`）は既に在ったのに、それを伝える門が無かった。
//
// 🔴 **依存ごとに訊く。** 「Docker がある」を 1 つの真偽値へまとめると、外部供給の有無を依存ごとに区別できなくなる。
// 🔴 **CI だけ「Docker あり」と答える近道（MSP の `CI=true` 分岐）は写さない**（MSP/ADR-0090 が退けた案 B
// 「同じコードが環境で違う結果を出す」）。CI で依存が揃わなかった実行は `scripts/check-integration-skips.js`
// （integration.yml）が skip 件数の上限 0 で赤にする —— 門と担保は対で 1 つの統制である（同じ PR で置いた）。
internal static class RequiredServices
{
    private const string LocalInfraHint =
        "（scripts/e2e-local-infra.sh up で 3 つをまとめて起こし、eval \"$(scripts/e2e-local-infra.sh env)\" で与えられる。"
        + "E2E_POSTGRES_CONNECTION と E2E_RABBITMQ_CONNECTION は両方そろえること）";

    /// <summary>PostgreSQL。</summary>
    public static ExternallySuppliable Postgres { get; } = new(
        "PostgreSQL",
        "E2E_POSTGRES_CONNECTION",
        () => E2EInfrastructure.PostgresConnection,
        "Host=localhost;Port=55432;Database=e2e;Username=e2e;Password=e2e");

    /// <summary>メッセージブローカ（RabbitMQ）。</summary>
    public static ExternallySuppliable RabbitMq { get; } = new(
        "RabbitMQ",
        "E2E_RABBITMQ_CONNECTION",
        () => E2EInfrastructure.RabbitMqConnection,
        "amqp://guest:guest@localhost:55672");

    /// <summary>認可サーバ（Keycloak。dev realm を import 済みであること）。</summary>
    public static ExternallySuppliable Keycloak { get; } = new(
        "Keycloak",
        "E2E_KEYCLOAK_BASEURL",
        () => E2EInfrastructure.KeycloakBaseUrl,
        "http://localhost:58080");

    /// <summary>
    /// 要る依存を**すべて**得られるのでなければ、試験を**真の Skipped** にする。
    /// 試験クラスのコンストラクタの先頭（コンテナを組み立てる前）で呼ぶ。
    /// </summary>
    /// <remarks>
    /// 🔴 `if (...) return;` のソフトスキップにしない（走っていないのに Passed になる）。
    /// 🔴 skip の理由に「どうすれば走るか」を書く（MSP/ADR-0090 決定 2）。
    /// </remarks>
    public static void SkipUnlessObtainable(params ExternallySuppliable[] needs)
    {
        var reason = SkipReason(needs, ContainerRuntime.IsReachable);
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
    }

    /// <summary>
    /// 得られない依存があれば skip の理由を、すべて得られるなら <c>null</c> を返す（判定の純粋部。単体試験が直接呼ぶ）。
    /// </summary>
    /// <param name="needs">試験が要る依存。</param>
    /// <param name="containerRuntimeReachable">コンテナ実行環境に届くか。外部供給の無い依存があるときだけ呼ぶ。</param>
    internal static string? SkipReason(IReadOnlyCollection<ExternallySuppliable> needs, Func<bool> containerRuntimeReachable)
    {
        var notSupplied = needs.Where(n => n.External is null).ToList();
        // 外部供給がすべて揃っていれば Docker へは問い合わせない（届かない環境での待ちを払わない）。
        if (notSupplied.Count == 0 || containerRuntimeReachable())
        {
            return null;
        }

        return "この試験が要るサービスを得られない: "
            + string.Join(" / ", notSupplied.Select(m => m.Name))
            + "。コンテナ実行環境（Testcontainers が使う Docker Engine API）を起動するか、"
            + "次の環境変数へ外部の端点を与えること —— "
            + string.Join(" / ", notSupplied.Select(m => $"{m.Variable}={m.Example}"))
            + LocalInfraHint;
    }
}

/// <summary>依存 1 つ分の記述。**外部から与えられるか、コンテナで起こせるかのどちらか**で得られる。</summary>
internal sealed class ExternallySuppliable(string name, string variable, Func<string?> external, string example)
{
    public string Name { get; } = name;

    public string Variable { get; } = variable;

    public string Example { get; } = example;

    /// <summary>外部から与えられた端点（未設定・空白なら <c>null</c>）。</summary>
    public string? External => external();
}

// NFR, IADR-0497 (#1200): コンテナ実行環境に届くか。**Testcontainers 自身の判定**をそのまま使う。
//
// 🔴 ソケットファイルの有無（MSP の `DockerRequired.IsAvailable()` の形）は答えにならない ——
// `/var/run/docker.sock` は在るがデーモンに繋がらない環境で、ファイルの有無は「使える」と答え、試験は fail のまま残った（実測）。
// Testcontainers は `DOCKER_HOST`・設定ファイル・既定のソケットを順に試し、繋がる端点が無ければ
// `DockerEndpointAuthConfig` を null にする（そのとき `Build()` が `DockerUnavailableException` を投げる）。同じ問いを門でも訊く。
internal static class ContainerRuntime
{
    private static readonly Lazy<bool> Reachable = new(Probe);

    /// <summary>プロセス内で 1 回だけ評価して持つ（試験ごとに Docker を探らない）。</summary>
    public static bool IsReachable() => Reachable.Value;

    private static bool Probe()
    {
        try
        {
            return TestcontainersSettings.OS.DockerEndpointAuthConfig is not null;
        }
        catch (Exception ex)
        {
            // 判定の途中で落ちる環境は「届かない」とする（skip の理由が手当てを伝える）。原因は標準エラーへ残す。
            Console.Error.WriteLine($"[RequiredServices] コンテナ実行環境の判定に失敗したため、届かないとして扱う: {ex.Message}");
            return false;
        }
    }
}
