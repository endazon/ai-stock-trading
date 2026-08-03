using AwesomeAssertions;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// issue #82 / IADR-0049 補足: 外部インフラ注入の判定ロジックの回帰テスト。
// Docker もコンテナも要らない純粋な環境変数ロジックのため Category=Integration は付けない（既定 CI で回す）。
// 本アセンブリは並列実行を無効化済み（AssemblyInfo.cs）なので、プロセスグローバルな環境変数を
// 一時的に差し替えても他テストと競合しない。各テストで必ず元へ戻す。
public class E2EInfrastructureTests
{
    private const string PostgresVar = "E2E_POSTGRES_CONNECTION";
    private const string RabbitMqVar = "E2E_RABBITMQ_CONNECTION";

    private static IDisposable Env(string? postgres, string? rabbitMq)
    {
        var restore = new EnvScope(Environment.GetEnvironmentVariable(PostgresVar),
            Environment.GetEnvironmentVariable(RabbitMqVar));
        Environment.SetEnvironmentVariable(PostgresVar, postgres);
        Environment.SetEnvironmentVariable(RabbitMqVar, rabbitMq);
        return restore;
    }

    private sealed class EnvScope(string? postgres, string? rabbitMq) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PostgresVar, postgres);
            Environment.SetEnvironmentVariable(RabbitMqVar, rabbitMq);
        }
    }

    [Fact]
    public void 両方未設定なら_UseExternal_false_で_Testcontainers_経路()
    {
        using var _ = Env(null, null);

        E2EInfrastructure.UseExternal.Should().BeFalse();
    }

    [Fact]
    public void 両方設定なら_UseExternal_true_で_外部注入経路()
    {
        using var _ = Env("Host=localhost;Port=1;Database=d;Username=u;Password=p", "amqp://guest:guest@localhost:2");

        E2EInfrastructure.UseExternal.Should().BeTrue();
    }

    // 片方だけの設定は「Testcontainers を起動したのに接続先は外部」という食い違いを生み、
    // 起動していない/無関係な対象を静かに検証してしまう。明示的に停止すること（claude-review 指摘の回帰）。
    [Fact]
    public void Postgres_だけ設定は例外で停止する_静かな誤検証の防止()
    {
        using var _ = Env("Host=localhost;Port=1;Database=d;Username=u;Password=p", null);

        var act = () => E2EInfrastructure.UseExternal;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*両方設定するか両方未設定*");
    }

    [Fact]
    public void RabbitMq_だけ設定は例外で停止する_静かな誤検証の防止()
    {
        using var _ = Env(null, "amqp://guest:guest@localhost:2");

        var act = () => E2EInfrastructure.UseExternal;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*両方設定するか両方未設定*");
    }

    // 空文字は「未設定」と同じ扱い（export E2E_X= のような指定を誤検知しない）。
    [Fact]
    public void 空文字は未設定として扱う()
    {
        using var _ = Env("", "");

        E2EInfrastructure.UseExternal.Should().BeFalse();
    }
}
