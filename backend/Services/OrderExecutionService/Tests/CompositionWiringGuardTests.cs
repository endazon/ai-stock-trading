using AiStockTrading.TestSupport.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 NFR, #947, IADR-0397, IADR-0163 決定2: **本番の組み立て（Program.cs）に配線の抜けが無い**ことを機械的に表明する。
// 規則（W0 組めない / W1 省略可能依存の未解決 / W2 渡し忘れ / W3 偽物の陰の本物）と所見への対処は IADR-0397。
//
// 🔴 **発注先の分岐（内蔵 paper / moomoo）ごとに組み立てが違う**ため、両方の構成で走らせる。
// PR #918 の形（Program.cs が ProtectiveStopDriftAdopter へ建玉照会の代わりに null を渡しても 755 件すべて緑）は
// moomoo 構成でしか現れない（paper では建玉照会そのものが登録されず、null が正しい）。本ガードでは moomoo 構成の
// W2 `ProtectiveStopDriftAdopter.positions` で赤になる（IADR-0397 の変異再注入の実測）。
// 片方の構成で「正当な不在」として許す依存は、もう片方の構成で結線を検査している（allowlist の各行に書く）。
// 母集団の実測（develop 3d9b91c2）: paper roots=22 handlers=4 fields=63 / moomoo roots=33 handlers=4 fields=99。
public class CompositionWiringGuardTests(ITestOutputHelper output)
{
    // 🔴 無視リストではなくラチェットである（所見が消えた行も赤）。1 行ごとに理由・外す条件・issue 番号を書く。
    private static readonly IReadOnlyDictionary<string, string> PaperAllowlist =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["W1 OrderExecutionService.Hosted.BrokerAvailabilityProbeService(accountSource)"] =
                "#375 / IADR-0153 決定2: 内蔵 paper は外部へ発注せずブローカー口座が存在しないため、口座種別の供給元を"
                + "登録しない（未登録なら probe は口座種別を発行せず、リスク管理側がフェイルクローズする）。"
                + "結線は moomoo 構成のガードが検査する。外す条件: paper でも口座種別を供給するようになったとき。",
        };

    private static readonly IReadOnlyDictionary<string, string> MoomooAllowlist =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["W1 OrderExecutionService.Features.OrderExecution.AmendOrder.OrderAmendmentService(amendmentBroker)"] =
                "#154 / IADR-0067 決定3（#847 で訂正だけに縮小）: 訂正（ModifyOrderAsync）はペーパー専用で、実 OpenD へ "
                + "TrdModifyOrder を配線していないため moomoo 構成では IOrderAmendmentBroker を型として与えない"
                + "（実行時も NotSupportedException で閉じる）。結線は paper 構成のガードが検査する。"
                + "外す条件: moomoo の訂正を配線したとき。",
        };

    [Fact]
    public void 本番の組み立てに配線の抜けが無い_内蔵paper構成()
    {
        using var factory = new ExecutionWorkerWebApplicationFactory();
        var report = factory.InspectComposition(typeof(CompositionWiringGuardTests).Assembly);
        output.WriteLine(report.Describe("OrderExecutionService[paper]"));
        report.AssertPopulation(minRoots: 11, minFields: 31, minHandlers: 2);
        report.AssertNoUnexpectedFindings(PaperAllowlist);
    }

    [Fact]
    public void 本番の組み立てに配線の抜けが無い_moomoo構成()
    {
        using var factory = new MoomooFactory();
        var report = factory.InspectComposition(typeof(CompositionWiringGuardTests).Assembly);
        output.WriteLine(report.Describe("OrderExecutionService[moomoo]"));
        report.AssertPopulation(minRoots: 16, minFields: 49, minHandlers: 2);
        report.AssertNoUnexpectedFindings(MoomooAllowlist);
    }

    // moomoo（SIMULATE）構成。差し替えるのは伝送の境界（OpenD クライアント）と DB だけで、
    // MoomooBrokerAdapter とそこから Program.cs が変換する建玉照会・口座種別・稼働観測は本物のまま組ませる。
    private sealed class MoomooFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 発注先の選択は Program.cs の最上段で読まれるため、ホスト設定（UseSetting）で渡す。
            builder.UseSetting("Broker:Provider", "moomoo");
            builder.UseSetting("Broker:Environment", "sim");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMoomooTradeClient>();
                services.AddSingleton(TransportStub.Create<IMoomooTradeClient>());
                // FR-10, #967, IADR-0425: 借株可否の照会ポートも同じ OpenD クライアントの別の面であり、伝送の境界として差し替える。
                services.RemoveAll<IShortPermitSource>();
                services.AddSingleton(TransportStub.Create<IShortPermitSource>());

                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<OrderExecutionDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(OrderExecutionDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                services.DisableAllExternalWolverineTransports();
            });
        }
    }
}
