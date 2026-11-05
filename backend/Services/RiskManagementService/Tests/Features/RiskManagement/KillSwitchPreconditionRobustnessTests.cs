using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace RiskManagementService.Tests;

// 🔴 FR-10, FR-14, #1534, IADR-0620: 版の前提条件の頑健さ。
// - 版は時刻の tick ではなく単調増加の値（同じ時刻の 2 回の書き込み・時計の戻りでも同じ版が再び現れない＝ABA にならない）。
// - 「版が違う」（409）として扱うのは、比較の不一致・直列化の失敗（40001）・デッドロック（40P01）・楽観排他の失敗だけ。
//   接続断・タイムアウト等の DB の障害は再送出する（5xx）。DB の障害を「状態が変わった」と見せない。
// - If-Match の `*`・複数の値は解釈しない（400）。409 の本文の code で、版の競合を他の理由と区別できる。
public class KillSwitchPreconditionRobustnessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 同じ時刻に2回書いても版は進み_古い版の解除は通らない()
    {
        var store = new InMemoryKillSwitchStore();
        var clock = new FakeClock(T0, DateOnly.FromDateTime(T0.UtcDateTime));
        var service = new KillSwitchService(store, new InMemorySettingsChangeLog(), clock);

        service.Engage("owner", "急変");
        var seen = service.GetState().Version;

        // 時計が進まない（または戻る）まま、別の操作が解除して再発動した。
        service.Disengage("owner-b", "回復", seen);
        service.Engage("owner-b", "再発");

        var stale = () => service.Disengage("owner", "見た状態で解除", seen);
        stale.Should().Throw<KillSwitchVersionConflictException>();
        service.GetState().Engaged.Should().BeTrue();
        service.GetState().Version.Should().Be("3");
    }

    [Fact]
    public void EFの版は書くたびに1つ進む()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var store = new EfKillSwitchStore(db);
        store.GetState().Version.Should().Be("0");

        store.SetState(new KillSwitchState(true, "owner", "急変", T0));
        store.GetState().Version.Should().Be("1");
        store.TrySetState(new KillSwitchState(false, "owner", "回復", T0), "1").Should().BeTrue();
        store.GetState().Version.Should().Be("2");
        store.TrySetState(new KillSwitchState(true, "owner", "古い", T0), "1").Should().BeFalse();
        store.GetState().Version.Should().Be("2");
    }

    public static TheoryData<string, bool> Failures() => new()
    {
        { "concurrency", true },
        { "serialization", true },
        { "deadlock", true },
        { "connection", false },
        { "timeout", false },
        { "unique", true },
        { "foreign-key", false },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void 版の競合として扱う失敗は直列化とデッドロックと楽観排他と初回の作成の主キー違反だけ(string kind, bool conflict) =>
        EfKillSwitchStore.IsConflict(Failure(kind)).Should().Be(conflict);

    [Fact]
    public void 競合でない保存の失敗は再送出し_状態は変わらない()
    {
        var name = Guid.NewGuid().ToString();
        using (var seed = NewContext(name))
            new EfKillSwitchStore(seed).SetState(new KillSwitchState(true, "owner", "急変", T0));

        using var db = NewContext(name, new ThrowOnSave(Failure("connection")));
        var store = new EfKillSwitchStore(db);
        var write = () => store.TrySetState(new KillSwitchState(false, "owner", "回復", T0), "1");

        write.Should().Throw<DbUpdateException>();
        using var check = NewContext(name);
        new EfKillSwitchStore(check).GetState().Engaged.Should().BeTrue();
    }

    [Fact]
    public void 楽観排他の失敗は競合として_false_を返し_追跡を外す()
    {
        var name = Guid.NewGuid().ToString();
        using (var seed = NewContext(name))
            new EfKillSwitchStore(seed).SetState(new KillSwitchState(true, "owner", "急変", T0));

        using var db = NewContext(name, new ThrowOnSave(Failure("concurrency")));
        var store = new EfKillSwitchStore(db);

        store.TrySetState(new KillSwitchState(false, "owner", "回復", T0), "1").Should().BeFalse();
        db.ChangeTracker.Entries<KillSwitchRow>().Should().BeEmpty("失敗した書き込みを同じスコープの後続の保存へ持ち越さない");
    }

    // 🔴 前提条件なしの書き込み（段 1・gRPC・撤退の自動発動）は、並行の競合で失敗させない（最後の書き込みが勝つ従前の挙動）。
    [Fact]
    public void 前提条件なしの書き込みは競合したら読み直して書き直す()
    {
        var name = Guid.NewGuid().ToString();
        using (var seed = NewContext(name))
            new EfKillSwitchStore(seed).SetState(new KillSwitchState(false, "owner", "初期", T0));

        var interceptor = new ThrowOnSave(Failure("concurrency"), times: 2);
        using var db = NewContext(name, interceptor);
        new EfKillSwitchStore(db).SetState(new KillSwitchState(true, "auto", "撤退", T0));

        interceptor.Calls.Should().Be(3);
        using var check = NewContext(name);
        var state = new EfKillSwitchStore(check).GetState();
        state.Engaged.Should().BeTrue();
        state.Version.Should().Be("2");
    }

    [Fact]
    public void 前提条件なしの書き込みは上限まで競合し続けたら例外を返す()
    {
        var interceptor = new ThrowOnSave(Failure("concurrency"));
        using var db = NewContext(Guid.NewGuid().ToString(), interceptor);
        var write = () => new EfKillSwitchStore(db).SetState(new KillSwitchState(true, "auto", "撤退", T0));

        write.Should().Throw<DbUpdateConcurrencyException>();
        interceptor.Calls.Should().Be(EfKillSwitchStore.MaxWriteAttempts);
    }

    [Fact]
    public void 前提条件なしの書き込みは競合でない失敗を再試行しない()
    {
        var interceptor = new ThrowOnSave(Failure("connection"));
        using var db = NewContext(Guid.NewGuid().ToString(), interceptor);
        var write = () => new EfKillSwitchStore(db).SetState(new KillSwitchState(true, "auto", "撤退", T0));

        write.Should().Throw<DbUpdateException>();
        interceptor.Calls.Should().Be(1);
    }

    // ── REST（If-Match・code）────────────────────────────────────────────────

    private static HttpClient Owner(RiskWorkerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "trading-owner");
        return client;
    }

    private static async Task<string> VersionOf(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/risk-controls/kill-switch"));
        return doc.RootElement.GetProperty("version").GetString()!;
    }

    private static HttpRequestMessage Disengage(params string[] ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/risk-controls/kill-switch/disengage")
        {
            Content = JsonContent.Create(new KillSwitchRequest("回復")),
        };
        foreach (var value in ifMatch)
            request.Headers.TryAddWithoutValidation("If-Match", value);
        return request;
    }

    [Theory]
    [InlineData("*")]
    [InlineData("\"1\", \"2\"")]
    public async Task If_Matchの星と複数の値は400で_状態を変えない(string ifMatch)
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = Owner(factory);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("急変"))).EnsureSuccessStatusCode();

        var response = await client.SendAsync(Disengage(ifMatch));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain($"\"code\":\"{KillSwitchIfMatchUnsupportedException.Code}\"");
        (await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeTrue();
    }

    [Fact]
    public async Task If_Matchのヘッダーの重複も400()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = Owner(factory);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("急変"))).EnsureSuccessStatusCode();
        var version = await VersionOf(client);

        var response = await client.SendAsync(Disengage($"\"{version}\"", $"\"{version}\""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 古い版の解除は409で_codeは版の競合()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = Owner(factory);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("急変"))).EnsureSuccessStatusCode();
        var seen = await VersionOf(client);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("再発"))).EnsureSuccessStatusCode();

        var response = await client.SendAsync(Disengage($"W/\"{seen}\""));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain($"\"code\":\"{KillSwitchVersionConflictException.Code}\"");
    }

    [Fact]
    public async Task 正しい版のIf_Matchなら解除できる()
    {
        using var factory = new RiskWorkerWebApplicationFactory();
        var client = Owner(factory);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("急変"))).EnsureSuccessStatusCode();
        var seen = await VersionOf(client);

        var response = await client.SendAsync(Disengage($"\"{seen}\""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetFromJsonAsync<KillSwitchState>("/risk-controls/kill-switch"))!.Engaged.Should().BeFalse();
    }

    [Fact]
    public async Task 段2の前提条件の無い解除は428で_codeを持つ()
    {
        using var factory = new RiskWorkerWebApplicationFactory
        {
            HostSettings = new Dictionary<string, string?> { ["RiskControls:KillSwitch:Precondition"] = "Require" },
        };
        var client = Owner(factory);
        (await client.PostAsJsonAsync("/risk-controls/kill-switch/engage", new KillSwitchRequest("急変"))).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/risk-controls/kill-switch/disengage", new KillSwitchRequest("回復"));

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionRequired);
        (await response.Content.ReadAsStringAsync()).Should().Contain($"\"code\":\"{KillSwitchPreconditionRequiredException.Code}\"");
    }

    // ── 補助 ─────────────────────────────────────────────────────────────

    private static Exception Failure(string kind) => kind switch
    {
        "concurrency" => new DbUpdateConcurrencyException("並行する更新"),
        "serialization" => new DbUpdateException("直列化", Postgres(PostgresErrorCodes.SerializationFailure)),
        "deadlock" => new InvalidOperationException("実行の失敗", Postgres(PostgresErrorCodes.DeadlockDetected)),
        "connection" => new DbUpdateException("接続", new NpgsqlException("接続が切れた")),
        "timeout" => new DbUpdateException("時間切れ", new TimeoutException()),
        "unique" => new DbUpdateException("一意", Postgres(PostgresErrorCodes.UniqueViolation)),
        "foreign-key" => new DbUpdateException("外部キー", Postgres(PostgresErrorCodes.ForeignKeyViolation)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PostgresException Postgres(string sqlState) => new("試験", "ERROR", "ERROR", sqlState);

    private static RiskManagementDbContext NewContext(string name, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<RiskManagementDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(interceptors)
            .Options);

    private sealed class ThrowOnSave(Exception failure, int times = int.MaxValue) : SaveChangesInterceptor
    {
        public int Calls { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) =>
            ++Calls <= times ? throw failure : result;
    }
}
