using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TestSupport.Messaging;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1083, FR-10, FR-06, FR-11, ADR-0040 決定1, #1002, IADR-0429 決定1・決定7: **本番の Program.cs の組み立て**
// （発注執行のハンドラの発見・DI・共通配線の conventional routing）を通して、承認を 1 通流すと損切りの実行機構の解決結果が
// **RabbitMQ の共有 exchange（メッセージ型の完全名）へ送られる**ことを固定する。監査サービスはこの exchange に購読キューを bind する。
//
// 差し替えるのは外界だけ（DB の置き場所と外部トランスポート。送信は stub へ記録され宛先 URI は保たれる）。ブローカーは Program.cs の既定
// （内蔵 paper＝moomoo SIMULATE ではない）のまま——S0 は一致、S2 は拒否（見送り）に解決される。
// ハンドラから発行を外す・結果へ載せるのを外す・ルーティングが exchange に向かない、のいずれでも本試験は落ちる。
public class StopLossMethodResolvedCompositionTests
{
    [Theory]
    [InlineData(StopLossExecutionMethod.BrokerStopOrder, StopLossExecutionMethod.BrokerStopOrder, StopLossMethodResolutionReason.AsSelected)]
    [InlineData(StopLossExecutionMethod.NoProtectiveStop, null, StopLossMethodResolutionReason.BrokerNotMoomooSimulate)]
    public async Task T_10_1083_本番の組み立てで承認を流すと解決結果が共有exchangeへ送られる(
        StopLossExecutionMethod selected, StopLossExecutionMethod? applied, StopLossMethodResolutionReason reason)
    {
        await using var factory = new ExecutionWorkerWebApplicationFactory();
        var intent = new OrderIntent(
            "AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.InternalPaper,
            10, 100m, PositionEffect.Open, StopLossPrice: 95m);
        var approved = new OrderApproved(Guid.NewGuid(), intent, 10, DateTimeOffset.UtcNow, StopLossMethod: selected);

        var session = await factory.Services.ExecuteAndWaitForTestAsync(async () =>
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(approved);
        });

        var envelope = session.Sent.Envelopes().Should().ContainSingle(e => e.Message is StopLossMethodResolved).Which;
        envelope.Destination!.ToString()
            .Should().Be("rabbitmq://exchange/AiStockTrading.Shared.Contracts.Events.StopLossMethodResolved");
        var resolved = (StopLossMethodResolved)envelope.Message!;
        resolved.DecisionId.Should().Be(approved.DecisionId);
        resolved.SelectedMethod.Should().Be(selected);
        resolved.AppliedMethod.Should().Be(applied);
        resolved.Reason.Should().Be(reason);
        resolved.Provider.Should().Be(BrokerProvider.InternalPaper, "実際に発注するアダプタ（Program.cs の既定は内蔵 paper）");
    }
}
