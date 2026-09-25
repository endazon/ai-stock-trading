using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1035, FR-10, #967, IADR-0425 決定1: **moomoo 構成の本番の組み立て（Program.cs）で、借株可否の照会サービスが
// 照会ポート（IShortPermitSource）を受け取っている**こと。
//
// なぜ要るか: 照会サービスはポートを GetService（null 許容）で受け、null なら「分からない（broker-not-supported）」を返す。
// したがって Program.cs の moomoo 構成の登録（IShortPermitSource ← OpenD クライアント）を消しても、**コンパイルは通り、
// 既存のテストはすべて緑のまま**、本番は照会を 1 度もしなくなる（PR #1001 の監査が登録を消して 884 件全緑を実測）。
// 組み立てガードの moomoo 構成は照会ポートを伝送の境界として自分で差し替えるため、この消失を見ない。
// 本テストは差し替えを **OpenD クライアント（IMoomooTradeClient）だけ**に留め、照会ポートの登録は Program.cs のまま組ませる。
public class ShortPermitCompositionTests
{
    [Fact]
    public async Task moomoo構成の組み立ては借株可否の照会にOpenDクライアントの照会ポートを渡す()
    {
        await using var factory = new MoomooFactory();
        var client = factory.Client;

        var view = await factory.Services.GetRequiredService<ShortPermitQueryService>()
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        view.UnknownReason.Should().NotBe(ShortPermitUnknownReasons.BrokerNotSupported,
            "moomoo 構成では照会ポートが登録されていなければならない（Program.cs の IShortPermitSource の登録）");
        view.Status.Should().Be(ShortPermitStatus.Permitted);
        client.ShortPermitCalls.Should().Be(1, "照会は OpenD クライアント（発注と同じ接続）へ届く");
    }

    [Fact]
    public async Task 内蔵paper構成の組み立ては照会ポートを持たず分からないを返す()
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();

        var view = await factory.Services.GetRequiredService<ShortPermitQueryService>()
            .QueryAsync("AAPL", Market.UnitedStates, TestContext.Current.CancellationToken);

        view.Status.Should().Be(ShortPermitStatus.Unknown);
        view.UnknownReason.Should().Be(ShortPermitUnknownReasons.BrokerNotSupported);
    }

    // moomoo（SIMULATE）構成。差し替えるのは OpenD クライアント・DB・外部トランスポートと、外界へ出る常駐（Hosted）だけ。
    private sealed class MoomooFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        public FakeOpenDClient Client { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "moomoo");
            builder.UseSetting("Broker:Environment", "sim");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                // 🔴 IShortPermitSource には触れない（Program.cs の登録が IMoomooTradeClient から引く形を試す）。
                services.RemoveAll<IMoomooTradeClient>();
                services.AddSingleton<IMoomooTradeClient>(Client);

                // OpenD へ照会する常駐は起動させない（本テストの関心は組み立てだけ）。
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

    // OpenD クライアントの偽物。本物（MMApiMoomooTradeClient）と同じく発注の面と照会の面の両方を 1 つの実体が持つ。
    internal sealed class FakeOpenDClient : IMoomooTradeClient, IShortPermitSource
    {
        public int ShortPermitCalls { get; private set; }

        public Task<bool?> GetShortPermitAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            ShortPermitCalls++;
            return Task.FromResult<bool?>(true);
        }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
