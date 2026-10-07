using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// NFR, MSP/ADR-0090 決定 1・2, IADR-0497 (#1200): 統合テストの門（RequiredServices）の判定の純粋部の回帰テスト。
// Docker もコンテナも要らないため Category=Integration は付けない（既定 CI で回す）。
// 外部の値は環境変数ではなく関数で与える（プロセスグローバルな環境変数を触らない）。
public class RequiredServicesTests
{
    private static ExternallySuppliable Dep(string name, string? external) =>
        new(name, $"E2E_{name.ToUpperInvariant()}_VAR", () => external, $"{name}-example");

    [Fact]
    public void 外部供給があれば_コンテナ実行環境に届かなくても_得られる()
    {
        var reason = RequiredServices.SkipReason([Dep("Pg", "Host=x")], () => false);

        reason.Should().BeNull();
    }

    [Fact]
    public void 外部供給が無く_コンテナ実行環境に届けば_得られる()
    {
        var reason = RequiredServices.SkipReason([Dep("Pg", null), Dep("Mq", null)], () => true);

        reason.Should().BeNull();
    }

    [Fact]
    public void 外部供給が無く_コンテナ実行環境にも届かなければ_どうすれば走るかを理由に書く()
    {
        var reason = RequiredServices.SkipReason([Dep("Pg", null), Dep("Mq", null)], () => false);

        // MSP/ADR-0090 決定 2: 不足している依存名・与える環境変数・値の例・まとめて起こすスクリプトを並べる。
        reason.Should().NotBeNull();
        reason.Should().Contain("Pg").And.Contain("Mq");
        reason.Should().Contain("E2E_PG_VAR=Pg-example").And.Contain("E2E_MQ_VAR=Mq-example");
        reason.Should().Contain("scripts/e2e-local-infra.sh");
    }

    [Fact]
    public void 依存ごとに訊く_外部供給のある依存は理由に出さない()
    {
        // 「Docker がある」を 1 つの真偽値へまとめない（MSP/ADR-0090 決定 1）。足りない依存だけを名指しする。
        var reason = RequiredServices.SkipReason([Dep("Pg", "Host=x"), Dep("Kc", null)], () => false);

        reason.Should().NotBeNull();
        reason.Should().Contain("E2E_KC_VAR=Kc-example");
        reason.Should().NotContain("E2E_PG_VAR");
    }

    [Fact]
    public void すべて外部供給なら_コンテナ実行環境に問い合わせない()
    {
        var probed = false;

        var reason = RequiredServices.SkipReason(
            [Dep("Pg", "Host=x"), Dep("Mq", "amqp://x")],
            () =>
            {
                probed = true;
                return false;
            });

        reason.Should().BeNull();
        probed.Should().BeFalse("外部供給だけで揃うなら Docker を探る待ちを払わない");
    }

    [Fact]
    public void 実在の依存は_E2EInfrastructure_の環境変数を名指しする()
    {
        // 外部供給の口は新設せず E2EInfrastructure の既存の変数を使う（planning#575 の完了記録「新設は要らない」）。
        RequiredServices.Postgres.Variable.Should().Be("E2E_POSTGRES_CONNECTION");
        RequiredServices.RabbitMq.Variable.Should().Be("E2E_RABBITMQ_CONNECTION");
        RequiredServices.Keycloak.Variable.Should().Be("E2E_KEYCLOAK_BASEURL");
    }
}
