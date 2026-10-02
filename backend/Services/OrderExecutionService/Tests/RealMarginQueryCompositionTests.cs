using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 追記）, #1000, IADR-0482 決定2・3（T-10-2062 / T-10-2063 / T-10-2065）:
// 本番の組み立て（Program.cs）での実弾口座の読み取り専用の照会。
//   - 既定（未設定）は無効: 照会ポートは従来どおり発注と同じ OpenD クライアント（SIMULATE）で、実弾の照会の型は登録されない。
//   - 有効: 照会ポートだけが実弾の照会クライアントに替わり、発注経路（OpenD クライアント・アダプタ・予約の照会）は実弾の照会クライアントを掴まない。
//   - moomoo 以外で有効にしたら起動時に止める。実弾（live 階層）は有効にしても閂（LiveTradingGate）が先に止める。
public class RealMarginQueryCompositionTests
{
    // T-10-2062: 既定は無効。
    [Fact]
    public async Task 既定では実弾の照会を組まず照会は発注と同じOpenDクライアントへ届く()
    {
        await using var factory = new MoomooFactory(realMarginQueryEnabled: null);

        var view = await factory.Services.GetRequiredService<ShortPermitQueryService>()
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        view.Status.Should().Be(ShortPermitStatus.Permitted);
        factory.TradeClient.ShortPermitCalls.Should().Be(1, "既定は従来どおり SIMULATE の OpenD クライアントで照会する");
        factory.OpenD.Requests.Should().BeEmpty("既定では実弾のヘッダを 1 度も作らない");
        factory.Services.GetService<MMApiRealMarginQueryClient>().Should().BeNull();
        factory.Services.GetService<IRealReadOnlyQueryAudit>().Should().BeNull();
        factory.Services.GetRequiredService<IShortPermitSource>().Should().BeSameAs(factory.TradeClient);
    }

    // T-10-2065: 有効にすると照会だけが実弾口座のヘッダへ替わり、照会ごとに監査が出る。
    [Fact]
    public async Task 有効にすると照会だけが実弾口座のヘッダへ替わり監査が出る()
    {
        await using var factory = new MoomooFactory(realMarginQueryEnabled: "true");

        var view = await factory.Services.GetRequiredService<ShortPermitQueryService>()
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        view.Status.Should().Be(ShortPermitStatus.Permitted);
        factory.TradeClient.ShortPermitCalls.Should().Be(0, "照会は発注の OpenD クライアントを通らない");
        factory.OpenD.Requests.Should().ContainSingle()
            .Which.C2S.Header.TrdEnv.Should().Be((int)TrdCommon.TrdEnv.TrdEnv_Real);
        factory.Audit.Entries.Should().ContainSingle().Which.TradingEnvironment.Should().Be("Real");
    }

    // T-10-2063: 発注経路のどの口も実弾の照会クライアントを掴まない（DI の上の切り離し）。
    [Fact]
    public async Task 有効でも発注経路は実弾の照会クライアントを掴まない()
    {
        await using var factory = new MoomooFactory(realMarginQueryEnabled: "true");
        var services = factory.Services;
        var real = services.GetRequiredService<MMApiRealMarginQueryClient>();

        services.GetRequiredService<IShortPermitSource>().Should().BeSameAs(real);
        services.GetRequiredService<IMoomooTradeClient>().Should().BeSameAs(factory.TradeClient);
        object?[] orderPath =
        [
            services.GetService<IBrokerAdapter>(),
            services.GetService<IReservationBrokerProbe>(),
            services.GetService<IBrokerPositionSource>(),
            services.GetService<IBrokerAccountSource>(),
        ];
        orderPath.Should().NotContain(o => ReferenceEquals(o, real));
        // 本番の監査は Wolverine で出す実装（試験の差し替えは Audit だけ。登録の型を確かめる）。
        factory.RegisteredAuditImplementation.Should().Be(typeof(WolverineRealReadOnlyQueryAudit));
    }

    // T-10-2065（否定形）: moomoo 以外で有効にしたら起動時に止める。
    [Fact]
    public void 内蔵paper構成で有効にすると起動時に止める()
    {
        using var factory = new PaperFactory();

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{RealMarginQueryOptions.EnabledKey}*");
    }

    // T-10-2065（否定形）: 実弾（live 階層）は、照会を有効にしても閂（LiveTradingGate）が先に止める＝閂は変わらない。
    [Fact]
    public void 実弾階層は照会を有効にしても閂が止める()
    {
        using var factory = new MoomooFactory(realMarginQueryEnabled: "true", environment: "live");

        var act = () => factory.Services;

        act.Should().Throw<InvalidOperationException>().WithMessage("*IADR-0056*").Which.Message.Should().Contain("moomoo-live");
    }

    private sealed class PaperFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "paper");
            builder.UseSetting(RealMarginQueryOptions.EnabledKey, "true");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            builder.ConfigureServices(services => services.DisableAllExternalWolverineTransports());
        }
    }

    // moomoo（SIMULATE）構成。差し替えるのは OpenD（発注のクライアント・照会の接続）・監査の出口・DB・外部トランスポート・常駐だけ。
    private sealed class MoomooFactory(string? realMarginQueryEnabled, string environment = "sim") : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        public ShortPermitCompositionTests.FakeOpenDClient TradeClient { get; } = new();

        public FakeMarginQueryOpenD OpenD { get; } = new(FakeMarginQueryOpenD.DefaultAccounts(),
            _ => FakeMarginQueryOpenD.Reply(0, "", FakeMarginQueryOpenD.Row("AAPL", true)));

        public RecordingRealReadOnlyQueryAudit Audit { get; } = new();

        public Type? RegisteredAuditImplementation { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "moomoo");
            builder.UseSetting("Broker:Environment", environment);
            if (realMarginQueryEnabled is not null)
                builder.UseSetting(RealMarginQueryOptions.EnabledKey, realMarginQueryEnabled);
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMoomooTradeClient>();
                services.AddSingleton<IMoomooTradeClient>(TradeClient);
                services.AddSingleton<IMoomooMarginQueryConnectionFactory>(OpenD);

                RegisteredAuditImplementation = services
                    .LastOrDefault(d => d.ServiceType == typeof(IRealReadOnlyQueryAudit))?.ImplementationType;
                if (RegisteredAuditImplementation is not null)
                {
                    services.RemoveAll<IRealReadOnlyQueryAudit>();
                    services.AddSingleton<IRealReadOnlyQueryAudit>(Audit);
                }

                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                      && d.ImplementationType?.Namespace == "OrderExecutionService.Hosted")
                             .ToList())
                {
                    services.Remove(hosted);
                }

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
