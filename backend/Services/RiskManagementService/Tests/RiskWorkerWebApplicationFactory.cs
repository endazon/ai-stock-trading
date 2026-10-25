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
    /// FR-10, #905, #1423 M-3, IADR-0354 決定3/4, IADR-0581 決定1: 基準資金の行を仕込む<b>観測時刻</b>を、
    /// 起点 <paramref name="now"/> の<b>直前の米国東部暦日</b>に置く。
    /// <para>
    /// 🔴 <b>「直前の暦日」でなければならない。</b> <c>EfCapitalBaselineStore</c> は <c>TradingDay &lt; today</c>
    /// （米国東部の暦日）の行しか使わず（#905）、さらに #1423 M-3（ADR-0073 決定1）以降は<b>直前の暦日の行が無ければ
    /// 未供給</b>（新規建ては <c>CapitalBaselineUnavailable</c> で止まる）である。旧来の「48 時間前」は時刻によって
    /// 2 暦日前に落ちるため使えない。24 時間前は夏時間が終わる 25 時間の日の最後の 1 時間に当日へ落ちる（#905）。
    /// よって時刻ではなく暦日で決める: 1 時間ずつ遡り、最初に前暦日へ入った時刻を採る（1 時間刻みは暦日を飛び越えない）。
    /// </para>
    /// </summary>
    internal static DateTimeOffset PreviousTradingDayObservation(DateTimeOffset now)
    {
        var today = Common.Abstractions.TradingDay.Of(now, AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates);
        var observedAt = now;
        while (Common.Abstractions.TradingDay.Of(
                   observedAt, AiStockTrading.Shared.Contracts.Trading.Market.UnitedStates) >= today)
        {
            observedAt = observedAt.AddHours(-1);
        }

        return observedAt;
    }

    /// <summary>
    /// FR-10, #1258: 基準資金の行を仕込む<b>起点の時刻</b>。既定（<c>null</c>）は実時刻。
    /// <para>
    /// 🔴 <c>FakeClock</c> を固定日へ置く試験は、<b>同じ時刻をここへ渡す</b>。<c>EfCapitalBaselineStore</c> は
    /// 偽の時計の当日（<c>TradingDay &lt; today</c>）より前の行しか使わないため、実時刻から仕込むと
    /// 実時刻が固定日の翌々日（ET）に入った時点で仕込みの行が当日扱いになり外れ、基準資金が <c>null</c> になる
    /// （新規建てが <c>CapitalBaselineUnavailable</c> で止まる。T-10-2320 が 2026-10-08 に決定的に赤になった）。
    /// </para>
    /// </summary>
    public DateTimeOffset? CapitalBaselineSeedNow { get; init; }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        if (CapitalBaselineEquityInBase is { } equity)
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RiskManagementDbContext>();
            var observedAt = PreviousTradingDayObservation(CapitalBaselineSeedNow ?? DateTimeOffset.UtcNow);
            // 取引日は米国東部時間の暦日（EfCapitalBaselineStore と同じ基準）。直前の暦日を仕込む
            // （根拠は PreviousTradingDayObservation の注記・#905・#1423 M-3）。
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
