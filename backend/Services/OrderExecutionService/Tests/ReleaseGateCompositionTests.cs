using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1609, NFR-09, ADR-0045 決定2, #1051, IADR-0444 決定2・決定4: **本番の Program.cs** の束縛で、
// 取引環境ごとの門（Reconciliation:ReleaseOnNotPlaced:Simulate / :Real。env の `__` は `:` に写る）が届くこと、
// 旧キー（スカラー）と同居しても束縛が落ちないこと、旧キーが SIMULATE の門にだけ写ること、不正な旧キーで起動が止まることを固定する。
//
// 殺す変異: Program.cs の旧キーの写像（PostConfigure）を消す（旧キー true の行が赤）／実弾の門にも写す（同）。
public class ReleaseGateCompositionTests
{
    private sealed class ProgramFactory(IReadOnlyDictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "paper");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);

            builder.ConfigureServices(services =>
            {
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

                // 自前の常駐は走らせない。見るのは Program.cs の構成の束縛だけである。
                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                services.DisableAllExternalWolverineTransports();
            });
        }
    }

    private static ReconciliationOptions Resolve(params (string Key, string Value)[] settings)
    {
        using var factory = new ProgramFactory(settings.ToDictionary(s => s.Key, s => s.Value));
        return factory.Services.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
    }

    [Fact]
    public void 新キーは取引環境ごとに届き_既定はどちらも閉()
    {
        var none = Resolve(("Reconciliation:Enabled", "true"));
        none.ReleaseOnNotPlaced.Simulate.Should().BeFalse("既定は閉");
        none.ReleaseOnNotPlaced.Real.Should().BeFalse("既定は閉");

        var simulateOnly = Resolve(("Reconciliation:ReleaseOnNotPlaced:Simulate", "true"));
        simulateOnly.ReleaseOnNotPlaced.Simulate.Should().BeTrue();
        simulateOnly.ReleaseOnNotPlaced.Real.Should().BeFalse("SIMULATE の門を開けても実弾の門は開かない");

        var realOnly = Resolve(("Reconciliation:ReleaseOnNotPlaced:Real", "true"));
        realOnly.ReleaseOnNotPlaced.Simulate.Should().BeFalse();
        realOnly.ReleaseOnNotPlaced.Real.Should().BeTrue();
    }

    [Fact]
    public void 稼働中の構成と同じ旧キーfalseは両方閉のまま起動する()
    {
        // 稼働中の PoC は旧キーを "false" で持つ（values.yaml を改める前の描画）。起動は止まらず、門はどちらも閉。
        var options = Resolve(("Reconciliation:Enabled", "true"), ("Reconciliation:ReleaseOnNotPlaced", "false"));

        options.ReleaseOnNotPlaced.Simulate.Should().BeFalse();
        options.ReleaseOnNotPlaced.Real.Should().BeFalse();
        options.LegacyReleaseOnNotPlacedMapped.Should().BeFalse();
    }

    [Fact]
    public void 旧キーtrueはSIMULATEの門にだけ写り_実弾の門は閉じたまま()
    {
        var options = Resolve(("Reconciliation:ReleaseOnNotPlaced", "true"));

        options.ReleaseOnNotPlaced.Simulate.Should().BeTrue();
        options.ReleaseOnNotPlaced.Real.Should().BeFalse("旧キーは実弾の門へ写らない");
    }

    [Fact]
    public void 旧キーと新キーが同居しても束縛は落ちず新キーが勝つ()
    {
        var options = Resolve(
            ("Reconciliation:ReleaseOnNotPlaced", "true"),
            ("Reconciliation:ReleaseOnNotPlaced:Simulate", "false"),
            ("Reconciliation:ReleaseOnNotPlaced:Real", "false"));

        options.ReleaseOnNotPlaced.Simulate.Should().BeFalse("新キーが勝つ");
        options.ReleaseOnNotPlaced.Real.Should().BeFalse();
        options.LegacyReleaseOnNotPlacedIgnored.Should().BeTrue();
    }

    [Fact]
    public void 旧キーが真偽値でなければ起動が止まる()
    {
        var act = () => Resolve(("Reconciliation:ReleaseOnNotPlaced", "open"));

        act.Should().Throw<Exception>().Where(e =>
            e.ToString().Contains("ReleaseOnNotPlaced:Simulate"), "不正な旧キーを黙って閉（や開）へ倒さない");
    }
}
