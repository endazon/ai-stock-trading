using RiskManagementService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RiskManagementService.Tests;

// WebApplicationFactory（platform Worker テスト準拠）。実 RabbitMQ/Postgres/Keycloak に依存せず、
// InMemory DB・Wolverine の外部トランスポート無効化（ADR-0013 / IADR-0129 / #354）・TestAuthHandler へ
// 差し替えてエンドポイントを検証する。
public sealed class RiskWorkerWebApplicationFactory : WebApplicationFactory<Program>
{
    // Factory ごとに一意な InMemory DB 名で他テストと隔離する。
    private readonly string _dbName = Guid.NewGuid().ToString();

    /// <summary>
    /// #257, IADR-0108: Program.cs が**登録時に**読む構成（例 <c>Risk:SimulatorProfile:Enabled</c>）を与える。
    /// <c>ConfigureAppConfiguration</c> の追加分は登録時読み取りに間に合わないため <c>UseSetting</c>（ホスト構成）で渡す。
    /// xUnit の <c>IClassFixture</c> は公開コンストラクタが 1 つであることを要求するため、引数ではなく初期化子で与える。
    /// </summary>
    public IDictionary<string, string?> HostSettings { get; init; } = new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in HostSettings)
            builder.UseSetting(key, value);

        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:ConnectionString"] = "amqp://localhost",
                ["Otlp:Endpoint"] = "http://localhost:4317",
                ["Auth:Authority"] = "https://localhost/realms/test",
            }));

        builder.ConfigureServices(services =>
        {
            ReplaceDbContextWithInMemory(services, _dbName);

            // ADR-0013, IADR-0129, #354: 実 RabbitMQ へ接続しない（ハンドラの発見は Program.cs 側の配線が担う）。
            services.DisableAllExternalWolverineTransports();

            // Keycloak/JWT に依存せず TestAuthHandler で認証する（既定スキームを Test に切替）。
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>
    /// FR-10, #869, ADR-0041 決定2, IADR-0354: 起動時に仕込む**基準資金**（前取引日の口座照会の観測）。
    /// <para>
    /// 🔴 <c>null</c> にすると「口座を照会できていない」状態になり、**新規建ては
    /// <c>CapitalBaselineUnavailable</c> で止まる**（fail-closed の検証はこちらを使う）。
    /// 既定は従前の台帳由来の基準資金と同額（<c>TradingDefaults.InitialCapital</c>）であり、
    /// 基準資金を関心に持たない既存テストの期待値を保つ。
    /// </para>
    /// </summary>
    public decimal? CapitalBaselineEquityInBase { get; init; } = Domain.TradingDefaults.InitialCapital;

    /// <summary>
    /// FR-10, #905, IADR-0354 決定3/4: 基準資金の行を仕込む**さかのぼり日数**（現在からの日数）。
    /// <para>
    /// 🔴 <b>1 ではなく 2 である。</b> <c>EfCapitalBaselineStore</c> は
    /// <c>TradingDay &lt; today</c>（<b>米国東部時間の暦日</b>）の行しか判定に使わないが、
    /// <c>AddDays(-1)</c> は暦日ではなく <b>24 時間前</b>である。夏時間が終わる日は 25 時間あるため、
    /// その日の最後の 1 時間（ET 23:00〜23:59 ＝ UTC 04:00〜04:59）だけ 24 時間前が<b>当日と同じ ET 暦日</b>へ落ち、
    /// 行が判定から外れて基準資金が <c>null</c> になる（新規建ては <c>CapitalBaselineUnavailable</c> で
    /// fail-closed に止まる）。48 時間前はどの瞬間でも前暦日に属し、鮮度上限（既定 4 日）の内側にも収まる。
    /// 同型の欠陥は結合試験側で PR #903（#893）が同じ値で是正済みである。
    /// </para>
    /// </summary>
    internal const int CapitalBaselineSeedDaysAgo = 2;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        if (CapitalBaselineEquityInBase is { } equity)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RiskManagementDbContext>();
            var observedAt = DateTimeOffset.UtcNow.AddDays(-CapitalBaselineSeedDaysAgo);
            // 取引日は米国東部時間の暦日（EfCapitalBaselineStore と同じ基準）。当日より前を仕込む
            // （さかのぼり日数の根拠は CapitalBaselineSeedDaysAgo の注記・#905）。
            var tradingDay = Common.Abstractions.TradingDay.Of(
                observedAt, AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates);
            // CreateHost は 1 つの factory につき複数回呼ばれ得る（InMemory DB は共有）ため冪等に書く。
            if (db.AccountEquityDays.Find(tradingDay) is null)
            {
                db.AccountEquityDays.Add(new AccountEquityDayRow
                {
                    TradingDay = tradingDay,
                    EquityInBase = equity,
                    ObservedAtUtc = observedAt,
                    UpdatedAt = observedAt,
                });
                db.SaveChanges();
            }
        }

        return host;
    }

    private static void ReplaceDbContextWithInMemory(IServiceCollection services, string dbName)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<RiskManagementDbContext>)
                     || (d.ServiceType.IsGenericType
                         && d.ServiceType.GetGenericTypeDefinition().FullName?
                             .Contains("IDbContextOptionsConfiguration") == true
                         && d.ServiceType.GenericTypeArguments.Length == 1
                         && d.ServiceType.GenericTypeArguments[0] == typeof(RiskManagementDbContext)))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.AddDbContext<RiskManagementDbContext>(opt => opt.UseInMemoryDatabase(dbName));
    }
}
