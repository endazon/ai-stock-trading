using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Messaging;
using AwesomeAssertions;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Wolverine;
using Xunit;

namespace AiStockTrading.IntegrationTests;

// 🔴 NFR, FR-10, IADR-0645 決定 2（AC3-1）: 発行元の確かめの再送の記録を、**実 PostgreSQL の**置き場に残す。
// 検証器を作り直した（再起動の）後も、処理し終えた ID の再送を拒む。期限を過ぎた記録は読みに数えず、捨てられる。
// 表は起動時の移行の経路（EnsureCreatedAsync）で作る。2 つの置き場が同時に作っても落ちない。作る権限が無ければ明示の例外で起動を止める。
//
// [Trait("Category","Integration")]: 既定 CI では除外し、専用ワークフロー（integration.yml）で実走する（門は RequiredServices・IADR-0497）。
[Trait("Category", "Integration")]
public sealed class MessageOriginReplayStoreE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _postgres;

    private string _connectionString = string.Empty;

    public MessageOriginReplayStoreE2ETests()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Postgres);
        _postgres = E2EInfrastructure.UseExternal ? null : new PostgreSqlBuilder("postgres:16").Build();
    }

    public async ValueTask InitializeAsync()
    {
        if (_postgres is not null)
            await _postgres.StartAsync();
        _connectionString = E2EInfrastructure.PostgresConnection ?? _postgres!.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task 実DBの置き場は再起動の後も処理し終えた再送を段2で拒む()
    {
        var clock = new Clock();
        var pair = NewPair();
        var sent = new Envelope(new OrderExecuted(
            Guid.NewGuid(), "order-1", OrderStatus.Filled, 10, 190.25m, DateTimeOffset.UtcNow, BrokerProvider.MoomooSimulate));
        new MessageOriginSigningRule(
            MessageOriginCatalog.OrderExecution,
            MessageOriginSignature.Create(null, null, MessageOriginCatalog.OrderExecution, pair.Private), clock).Modify(sent);
        Envelope Delivery()
        {
            var received = new Envelope(sent.Message!);
            foreach (var (k, v) in sent.Headers)
                received.Headers[k] = v;
            return received;
        }

        var receiverKeys = MessageOriginSignature.Create(
            new Dictionary<string, string?>(StringComparer.Ordinal) { [MessageOriginCatalog.OrderExecution] = pair.Public });
        MessageOriginVerifier Verifier(IMessageOriginReplayStore? store) => new(
            MessageOriginMode.Enforce, receiverKeys, TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), clock,
            NullLogger<MessageOriginVerifier>.Instance, replayStore: store);

        await using (var before = new PostgresMessageOriginReplayStore(_connectionString))
        {
            await before.EnsureCreatedAsync(CancellationToken.None);
            var verifier = Verifier(before);
            var first = Delivery();
            (await verifier.AdmitAsync(first, CancellationToken.None)).Should().BeTrue();
            await verifier.MarkHandledAsync(first, CancellationToken.None);
        }

        clock.Now += TimeSpan.FromHours(2);
        await using (var after = new PostgresMessageOriginReplayStore(_connectionString))
        {
            await after.EnsureCreatedAsync(CancellationToken.None); // 再起動の後の移行の経路（表は既に在る＝冪等）
            var replay = Delivery();
            (await Verifier(after).CheckAsync(replay, CancellationToken.None)).Should().Be(MessageOriginCheck.Replayed);
            (await Verifier(after).AdmitAsync(replay, CancellationToken.None)).Should().BeFalse();
        }

        // 陰性対照: 置き場の無い検証器では通ってしまう（確かめているのが置き場であること）。
        (await Verifier(null).CheckAsync(Delivery(), CancellationToken.None)).Should().Be(MessageOriginCheck.Valid);
    }

    [Fact]
    public async Task 実DBの置き場は期限を過ぎた記録を数えず捨てる()
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var store = new PostgresMessageOriginReplayStore(_connectionString);
        await using var other = new PostgresMessageOriginReplayStore(_connectionString);

        // 2 つの置き場（レプリカ）が同時に表を作っても落ちない。
        await Task.WhenAll(store.EnsureCreatedAsync(CancellationToken.None), other.EnsureCreatedAsync(CancellationToken.None));
        await store.RecordAsync(id, now.AddHours(24), CancellationToken.None);

        (await store.ContainsAsync(id, now, CancellationToken.None)).Should().BeTrue();
        (await other.ContainsAsync(id, now.AddHours(25), CancellationToken.None)).Should().BeFalse("期限を過ぎた記録は数えない");

        // 既にある ID は遅い方の期限に揃える（短い期限で上書きしない）。
        await store.RecordAsync(id, now.AddHours(1), CancellationToken.None);
        (await store.ContainsAsync(id, now.AddHours(2), CancellationToken.None)).Should().BeTrue();

        await store.PruneAsync(now.AddHours(25), CancellationToken.None);
        (await store.ContainsAsync(id, now, CancellationToken.None)).Should().BeFalse("捨てた記録は在る時刻で見ても無い");
    }

    // 🔴 作る権限が無い利用者では、起動時の EnsureCreatedAsync が明示の例外で止まる（実行時にメッセージを受けてから落とさない）。
    // 陰性対照: 同じ利用者でも、表を作っていない置き場の読みは例外（拒否として捨てない）。DB の所有者（コンテナの既定の利用者）では作れる（上の試験）。
    [Fact]
    public async Task 作る権限の無い利用者では表を作れず起動時の明示の例外で止まる()
    {
        var role = "ast_noddl_" + Guid.NewGuid().ToString("N")[..8];
        var password = Guid.NewGuid().ToString("N");
        var schema = "s_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = NpgsqlDataSource.Create(_connectionString))
        {
            // 既定のスキーマ（search_path の先頭）を、CREATE を与えない専用のスキーマにする（public の既定の権限に依らない）。
            await using var cmd = admin.CreateCommand(
                $"CREATE ROLE {role} LOGIN PASSWORD '{password}'; CREATE SCHEMA {schema}; GRANT USAGE ON SCHEMA {schema} TO {role}; "
                + $"ALTER ROLE {role} SET search_path = {schema};");
            await cmd.ExecuteNonQueryAsync();
        }

        var limited = new NpgsqlConnectionStringBuilder(_connectionString) { Username = role, Password = password }.ConnectionString;
        await using var store = new PostgresMessageOriginReplayStore(limited);

        var lookup = async () => await store.ContainsAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);
        await lookup.Should().ThrowAsync<InvalidOperationException>("表を作っていない置き場の読みは例外");

        var ensure = () => store.EnsureCreatedAsync(CancellationToken.None);
        var thrown = await ensure.Should().ThrowAsync<MessageOriginReplayStoreSetupException>();
        thrown.Which.Message.Should().Contain("SQLSTATE 42501").And.NotContain(password);
        store.IsReady.Should().BeFalse();
    }

    private static (string Private, string Public) NewPair()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }
}
