using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Infrastructure.ExternalServices;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 FR-05, FR-10, #1339（利用者裁定 2026-10-14・最小是正）, IADR-0536 決定1: 承認経路の指値の決済（判断の利確・損切りほか）は
// 有効期限 DAY を明示して送る。保護逆指値（S0 の Stop。#1214 の対象）・S3・成行・新規建ての指値は有効期限を載せない（従来どおり）。
// 作業仕様書 20261014_1339_close-limit-tif-and-release §受け入れ基準 1・2。
public class CloseLimitTimeInForceTests
{
    private const ulong SimAccId = 724808UL;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private static OrderIntent CloseIntent(TradeSide side = TradeSide.Sell) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m,
            PositionEffect.Close);

    private static OrderIntent OpenIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Buy, ProductType.Cash, BrokerProvider.MoomooSimulate, 13, 347.93m);

    // T-10-2652（陽性）: 承認経路の指値の決済（remark つき・なし、売り決済・買い戻し）は Day を載せる。
    [Theory]
    [InlineData(TradeSide.Sell, true)]
    [InlineData(TradeSide.Sell, false)]
    [InlineData(TradeSide.Buy, true)]
    public async Task T_10_2652_承認経路の指値の決済は有効期限DAYを載せる(TradeSide side, bool withDecisionId)
    {
        var client = new RecordingClient();
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);

        if (withDecisionId)
            await adapter.PlaceOrderAsync(CloseIntent(side), Guid.NewGuid(), TestContext.Current.CancellationToken);
        else
            await adapter.PlaceOrderAsync(CloseIntent(side), TestContext.Current.CancellationToken);

        var sent = client.Requests.Should().ContainSingle().Subject;
        sent.Kind.Should().Be(MoomooOrderKind.Limit);
        sent.PositionEffect.Should().Be(PositionEffect.Close);
        sent.TimeInForce.Should().Be(MoomooTimeInForce.Day);
    }

    // T-10-2652（陰性）: 保護逆指値（S0）・S3 の代替種別・成行（S1 の決済・成行の手仕舞い）・新規建ての指値は有効期限を載せない。
    [Fact]
    public async Task T_10_2652_保護逆指値とS3と成行と新規建ては有効期限を載せない()
    {
        var client = new RecordingClient();
        var adapter = new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate);
        var ct = TestContext.Current.CancellationToken;

        await adapter.PlaceStopOrderAsync(CloseIntent(), 330m, Guid.NewGuid(), ct);
        await adapter.PlaceMarketOrderAsync(CloseIntent(), Guid.NewGuid(), ct);
        await adapter.PlaceOrderAsync(OpenIntent(), Guid.NewGuid(), ct);
        await adapter.PlaceAlternativeStopOrderAsync(CloseIntent(), 330m, 347.93m, Guid.NewGuid(), ct);
        await new MoomooBrokerAdapter(client, BrokerProvider.MoomooSimulate,
                alternativeStop: new MoomooAlternativeStopSettings(AlternativeProtectiveOrderType.TrailingStop))
            .PlaceAlternativeStopOrderAsync(CloseIntent(), 330m, 347.93m, Guid.NewGuid(), ct);

        client.Requests.Select(r => r.Kind).Should().Equal(
            MoomooOrderKind.Stop, MoomooOrderKind.Market, MoomooOrderKind.Limit,
            MoomooOrderKind.StopLimit, MoomooOrderKind.TrailingStop);
        client.Requests.Should().AllSatisfy(r => r.TimeInForce.Should().BeNull(
            "保護逆指値（#1214 の対象）・S3・成行・新規建ての有効期限は変えない"));
    }

    // T-10-2653: OpenD へ送る要求。Day なら C2S.TimeInForce=TimeInForce_DAY、null なら欄を立てない（保護逆指値の要求は従来どおり）。
    [Fact]
    public async Task T_10_2653_DayはTimeInForce_DAYで送りnullは欄を立てない()
    {
        var connection = new PlaceOrderConnection();
        using var client = new MMApiMoomooTradeClient(
            new MoomooBrokerOptions("opend", 11111) { ReplyTimeout = Timeout.InfiniteTimeSpan },
            NullLogger<MMApiMoomooTradeClient>.Instance,
            new Factory(connection));
        var ct = TestContext.Current.CancellationToken;

        await client.PlaceOrderAsync(
                new MoomooOrderRequest("AAPL", MoomooMarket.UnitedStates, MoomooSide.Sell, 13, 347.93m,
                    PositionEffect: PositionEffect.Close, TimeInForce: MoomooTimeInForce.Day), ct)
            .WaitAsync(Guard, ct);
        await client.PlaceOrderAsync(
                new MoomooOrderRequest("AAPL", MoomooMarket.UnitedStates, MoomooSide.Sell, 13, 0m,
                    Kind: MoomooOrderKind.Stop, TriggerPrice: 330m, PositionEffect: PositionEffect.Close), ct)
            .WaitAsync(Guard, ct);

        connection.Requests.Should().HaveCount(2);
        var close = connection.Requests[0].C2S;
        close.HasTimeInForce.Should().BeTrue();
        close.TimeInForce.Should().Be((int)TrdCommon.TimeInForce.TimeInForce_DAY);
        close.OrderType.Should().Be((int)TrdCommon.OrderType.OrderType_Normal);
        var stop = connection.Requests[1].C2S;
        stop.HasTimeInForce.Should().BeFalse("保護逆指値の有効期限は変えない（#1214 の実弾確認の後に決める）");
        stop.OrderType.Should().Be((int)TrdCommon.OrderType.OrderType_Stop);
        MMApiMoomooTradeClient.ToOpenDTimeInForce(MoomooTimeInForce.Day)
            .Should().Be((int)TrdCommon.TimeInForce.TimeInForce_DAY);
    }

    private sealed class RecordingClient : IMoomooTradeClient
    {
        public List<MoomooOrderRequest> Requests { get; } = [];

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(new MoomooOrderResult($"mo-{Requests.Count}", MoomooOrderState.Submitted, 0, 0m));
        }

        public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderResult?>(null);

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
            string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken ct = default) =>
            Task.FromResult<MoomooOrderSnapshot?>(null);

        public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MoomooPositionSnapshot>>([]);

        public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken ct = default) =>
            Task.FromResult<MoomooAccountType?>(MoomooAccountType.Margin);

        public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken ct = default) =>
            Task.FromResult<decimal?>(3_000m);
    }

    private sealed class Factory(PlaceOrderConnection connection) : IMoomooTradeConnectionFactory
    {
        public IMoomooTradeConnection Create() => connection;
    }

    // SIMULATE 口座 1 つを持つ OpenD の偽物。発注の要求を記録し、受理を返す。
    private sealed class PlaceOrderConnection : IMoomooTradeConnection
    {
        private readonly MMAPI_Conn _handle = new();
        private MMSPI_Conn? _connCallback;
        private MMSPI_Trd? _trdCallback;
        private uint _serial;
        private uint _packet;

        public List<TrdPlaceOrder.Request> Requests { get; } = [];

        public void SetClientInfo(string clientId, int clientVersion) { }

        public void SetConnCallback(MMSPI_Conn callback) => _connCallback = callback;

        public void SetTrdCallback(MMSPI_Trd callback) => _trdCallback = callback;

        public void SetRsaPrivateKey(string privateKeyPem) { }

        public bool InitConnect(string host, ushort port, bool encrypt)
        {
            _ = Task.Run(() => _connCallback?.OnInitConnect(_handle, 0, string.Empty));
            return true;
        }

        public void Close() { }

        public Moomoo.OpenApi.Pb.Common.PacketID NextPacketId() =>
            Moomoo.OpenApi.Pb.Common.PacketID.CreateBuilder().SetConnID(1).SetSerialNo(++_packet).Build();

        public uint GetAccList(TrdGetAccList.Request request)
        {
            var serial = ++_serial;
            var acc = TrdCommon.TrdAcc.CreateBuilder()
                .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate)
                .SetAccID(SimAccId)
                .SetAccType((int)TrdCommon.TrdAccType.TrdAccType_Margin)
                .BuildPartial();
            var response = TrdGetAccList.Response.CreateBuilder()
                .SetRetType(0)
                .SetRetMsg(string.Empty)
                .SetS2C(TrdGetAccList.S2C.CreateBuilder().AddAccList(acc).BuildPartial())
                .BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_GetAccList(_handle, serial, response));
            return serial;
        }

        public uint PlaceOrder(TrdPlaceOrder.Request request)
        {
            var serial = ++_serial;
            Requests.Add(request);
            var response = TrdPlaceOrder.Response.CreateBuilder()
                .SetRetType(0)
                .SetRetMsg(string.Empty)
                .SetS2C(TrdPlaceOrder.S2C.CreateBuilder().SetOrderID(7860264464660206071UL + serial).BuildPartial())
                .BuildPartial();
            _ = Task.Run(() => _trdCallback?.OnReply_PlaceOrder(_handle, serial, response));
            return serial;
        }

        public uint GetMarginRatio(TrdGetMarginRatio.Request request) => ++_serial;

        public uint GetOrderFee(TrdGetOrderFee.Request request) => ++_serial;

        public uint ModifyOrder(TrdModifyOrder.Request request) => ++_serial;

        public uint GetOrderList(TrdGetOrderList.Request request) => ++_serial;

        public uint GetHistoryOrderList(TrdGetHistoryOrderList.Request request) => ++_serial;

        public uint GetPositionList(TrdGetPositionList.Request request) => ++_serial;

        public uint GetFunds(TrdGetFunds.Request request) => ++_serial;

        public void Dispose() { }
    }
}
