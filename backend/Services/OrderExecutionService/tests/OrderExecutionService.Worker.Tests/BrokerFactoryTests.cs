using AiStockTrading.OrderExecution.Worker.Composable.Adapters;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Broker;
using FluentAssertions;
using Xunit;

namespace AiStockTrading.OrderExecution.Worker.Tests;

// FR-05, ADR-0002, IADR-0016, IADR-0111: ブローカ選択の安全ゲート（実弾防止）の検証。
public class BrokerFactoryTests
{
    private static BrokerSelection Selection(string? provider, string? environment = null) =>
        BrokerSelection.Parse(provider, environment);

    [Theory]
    [InlineData("paper")]
    [InlineData("Paper")]
    [InlineData(" PAPER ")]
    [InlineData(null)]
    [InlineData("")]
    public void 既定またはpaper指定はペーパーブローカを返す(string? provider)
    {
        BrokerFactory.Create(Selection(provider)).Should().BeOfType<PaperBrokerAdapter>();
    }

    [Fact]
    public void moomoo指定は_client提供時に_MoomooBrokerAdapter_を返す()
    {
        // #13: OpenD 接続（IMoomooTradeClient）を与えると moomoo アダプタ（SIMULATE 限定）を返す。
        BrokerFactory.Create(Selection("moomoo", "sim"), new StubMoomooClient())
            .Should().BeOfType<MoomooBrokerAdapter>();
    }

    [Fact]
    public void moomoo指定は_client未提供時は起動時に停止する_誤用防止()
    {
        // OpenD 未接続で moomoo を選ぶのは誤用。実弾は撃たないが、SIMULATE 発注も OpenD 無しでは不可のため停止。
        var act = () => BrokerFactory.Create(Selection("moomoo"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*OpenD*");
    }

    // IADR-0111 閂 0: live 階層はアダプタを生成しない。OpenD 接続を与えても到達しない。
    [Fact]
    public void live階層は_OpenD接続があってもアダプタを生成しない()
    {
        var act = () => BrokerFactory.Create(Selection("moomoo", "live"), new StubMoomooClient());

        act.Should().Throw<InvalidOperationException>().WithMessage("*IADR-0056*");
    }

    private sealed class StubMoomooClient : IMoomooTradeClient
    {
        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest r, CancellationToken ct = default) =>
            Task.FromResult(new MoomooOrderResult("x", MoomooOrderState.FilledAll, 0, 0m));
        public Task<MoomooOrderResult?> QueryOrderAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderResult?>(null);
        public Task CancelOrderAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);
        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>([]);
    }

    [Fact]
    public void 未知のプロバイダは停止する()
    {
        var act = () => Selection("etrade");

        act.Should().Throw<InvalidOperationException>();
    }
}
