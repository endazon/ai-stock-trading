using AiStockTrading.TestSupport.Composition;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderExecutionService.Features.OrderExecution.QueryShortPermit;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Trading;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-11, ADR-0027 決定4, ADR-0035 決定3, #1086, IADR-0484:
// **本番の Program.cs の組み立て**が、発注先に応じた「取得できない理由」の供給口を登録することを固定する。
// 単体の試験（UnsuppliedOrderExpenseSourceTests）は For() を直接呼ぶため、Program.cs が引数なしの既定
// （＝「未接続」）を登録しても通ってしまう。SIMULATE の間ずっと誤った理由が警告に残る生存変異をここで殺す。
public class OrderExpenseSourceCompositionTests
{
    private sealed class ProgramFactory(string provider) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 発注先の選択は Program.cs の最上段で読まれるため、ホスト設定（UseSetting）で渡す。
            builder.UseSetting("Broker:Provider", provider);
            builder.UseSetting("Broker:Environment", "sim");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                // 伝送の境界（OpenD）だけを差し替える。内蔵 paper では元々登録されないため無害。
                services.RemoveAll<IMoomooTradeClient>();
                services.AddSingleton(TransportStub.Create<IMoomooTradeClient>());
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

    private static async Task<string> UnavailableReasonFor(string provider)
    {
        await using var factory = new ProgramFactory(provider);
        var source = factory.Services.GetRequiredService<IOrderExpenseSource>();
        var lookup = await source.GetOrderExpensesAsync(new OrderExpenseQuery(
            Guid.NewGuid(), "ORD-1", "NVDA", Market.UnitedStates,
            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)));

        // 🔴 否定形: どの発注先でも供給はされない（空の明細＝「費用 0」も、推計の明細も返さない）。
        lookup.IsSupplied.Should().BeFalse();
        return lookup.UnavailableReason;
    }

    [Fact]
    public async Task moomoo_SIMULATE構成では照会がブローカーに拒否されることを理由に持つ()
    {
        var reason = await UnavailableReasonFor("moomoo");

        reason.Should().Be(UnsuppliedOrderExpenseSource.MoomooSimulateReason);
    }

    [Fact]
    public async Task 内蔵paper構成ではブローカーの費用が存在しないことを理由に持つ()
    {
        var reason = await UnavailableReasonFor("paper");

        reason.Should().Be(UnsuppliedOrderExpenseSource.InternalPaperReason);
    }
}
