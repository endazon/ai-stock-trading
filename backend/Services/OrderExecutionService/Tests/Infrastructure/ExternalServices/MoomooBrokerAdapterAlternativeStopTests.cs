using OrderExecutionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Xunit;

namespace OrderExecutionService.Tests;

// FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: moomoo アダプタの**代替注文種別**（S3）の写像と、
// 拒否理由（retType / retMsg）の持ち帰り。実 OpenD 不使用（fake client）。
public class MoomooBrokerAdapterAlternativeStopTests
{
    private sealed class FakeClient : IMoomooTradeClient
    {
        public MoomooOrderRequest? LastRequest { get; private set; }
        public MoomooOrderResult Result { get; set; } = new("mo-1", MoomooOrderState.Submitted, 0, 0m);
        public Func<Exception>? ThrowOnPlace { get; set; }

        public Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            if (ThrowOnPlace is not null) throw ThrowOnPlace();
            return Task.FromResult(Result);
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
    }

    // ロング建玉の保護レグ＝売りの決済意図（発火価格 950・エントリーの判断価格 1,000）。
    private static OrderIntent CloseIntent(TradeSide side = TradeSide.Sell, decimal price = 950m) =>
        new("AAPL", Market.UnitedStates, side, ProductType.Cash, BrokerProvider.MoomooSimulate, 10, price,
            PositionEffect.Close);

    private static MoomooBrokerAdapter Adapter(
        IMoomooTradeClient client, AlternativeProtectiveOrderType orderType, decimal offsetRatio = 0.01m) =>
        new(client, BrokerProvider.MoomooSimulate,
            alternativeStop: new MoomooAlternativeStopSettings(orderType, offsetRatio));

    [Fact]
    public async Task StopLimitは発火価格をAuxPriceへ指値を不利側へずらして送る()
    {
        var client = new FakeClient();
        var decisionId = Guid.NewGuid();

        var placement = await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(CloseIntent(), triggerPrice: 950m, entryReferencePrice: 1_000m, decisionId);

        var request = client.LastRequest!;
        request.Kind.Should().Be(MoomooOrderKind.StopLimit);
        request.TriggerPrice.Should().Be(950m);
        // 売り（ロングの保護）なので指値は発火価格の**下**（950 × 0.99 = 940.5）。同値だと急落時に約定しない。
        request.Price.Should().Be(940.5m);
        request.TrailValue.Should().BeNull();
        request.Remark.Should().Be(decisionId.ToString("N"));
        placement.OrderType.Should().Be(AlternativeProtectiveOrderType.StopLimit);
        placement.RejectReasonCode.Should().BeNull();
        placement.RejectReasonMessage.Should().BeNull();
    }

    // T-10-395: #844 の実測（稼働環境）。発火価格 332.35・ずらし 1% だと 329.0265 になり、
    // ブローカーが `The precision of Price in Place Order does not meet the specification.` で拒否した。
    // 刻みへ丸めてから送る（売りなので**切り下げ**＝約定しやすい側）。
    [Fact]
    public async Task StopLimitの指値は市場の刻みへ丸めて送る()
    {
        var client = new FakeClient();

        await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(
                CloseIntent(), triggerPrice: 332.35m, entryReferencePrice: 335.86m, Guid.NewGuid());

        var request = client.LastRequest!;
        request.Price.Should().Be(329.02m, "332.35 × 0.99 = 329.0265 を小数 2 桁へ切り下げる");
        request.TriggerPrice.Should().Be(332.35m, "発火価格は既に刻みに合っているので動かさない");
    }

    // T-10-396: 発火価格が刻みを外れていれば、そちらも丸めて送る（指値だけ直しても同じ拒否になる）。
    [Fact]
    public async Task StopLimitの発火価格も刻みへ丸めて送る()
    {
        var client = new FakeClient();

        await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(
                CloseIntent(), triggerPrice: 332.3512m, entryReferencePrice: 335.86m, Guid.NewGuid());

        client.LastRequest!.TriggerPrice.Should().Be(
            332.36m, "ロングの保護は**早く発火する側**（切り上げ）へ倒す");
    }

    [Fact]
    public async Task StopLimitの買戻しは指値を発火価格の上へずらす()
    {
        var client = new FakeClient();

        await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(
                CloseIntent(TradeSide.Buy, 1_050m), triggerPrice: 1_050m, entryReferencePrice: 1_000m, Guid.NewGuid());

        client.LastRequest!.Price.Should().Be(1_060.5m, "買戻しの不利側は上（1,050 × 1.01）");
    }

    [Fact]
    public async Task TrailingStopはトレール幅を発火価格とエントリー価格の差で送る()
    {
        var client = new FakeClient();

        var placement = await Adapter(client, AlternativeProtectiveOrderType.TrailingStop)
            .PlaceAlternativeStopOrderAsync(CloseIntent(), triggerPrice: 950m, entryReferencePrice: 1_000m, Guid.NewGuid());

        var request = client.LastRequest!;
        request.Kind.Should().Be(MoomooOrderKind.TrailingStop);
        request.TrailValue.Should().Be(50m, "|1,000 − 950|");
        request.TriggerPrice.Should().BeNull("トレーリングストップは発火価格を持たない（幅で指定する）");
        placement.OrderType.Should().Be(AlternativeProtectiveOrderType.TrailingStop);
    }

    // 🔴 #821 の目的そのもの: 拒否理由（retType / retMsg）を戻り値へ載せること。
    // T-10-450, #848, IADR-0117（改定 8）: 刺激を retType=1（SDK に存在しない値）から実測値の -1（Failed）へ直した。
    // 「拒否」と確認できるのは -1 だけであり、それ以外は届いたか不明として伝播する。
    [Fact]
    public async Task 代替注文種別の拒否はretTypeとretMsgを戻り値へ載せる()
    {
        var client = new FakeClient
        {
            ThrowOnPlace = () => new MoomooTradeRequestException(
                "PlaceOrder", MoomooRetType.Failed, "Paper trading does not support StopLimit order"),
        };

        var placement = await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(CloseIntent(), 950m, 1_000m, Guid.NewGuid());

        placement.Order.Status.Should().Be(OrderStatus.Rejected, "拒否は終端 Rejected へ倒す（従来どおり）");
        placement.RejectReasonCode.Should().Be(-1);
        placement.RejectReasonMessage.Should().Be("Paper trading does not support StopLimit order");
    }

    // 🔴 T-10-408, #848, IADR-0117（2026-09-19 追記・改定 6）: **送信後に結果を確認できなかった失敗は
    // S3 でも Rejected へ畳まない**（否定形）。理由文は呼び出し側（発注執行）が例外から取り出して
    // 試行の記録へ載せるため、監査に空欄は残らない。
    [Fact]
    public async Task 送信後に結果を確認できない失敗は_S3_でも_Rejected_へ畳まず伝播する_否定形()
    {
        var cause = new TimeoutException("SDK の返信待ちがタイムアウト（テスト）");
        var client = new FakeClient { ThrowOnPlace = () => cause };

        var act = async () => await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(CloseIntent(), 950m, 1_000m, Guid.NewGuid());

        var thrown = await act.Should().ThrowAsync<BrokerDispatchIndeterminateException>();
        thrown.Which.InnerException.Should().BeSameAs(cause);
    }

    // 接続確立の失敗は**確実に未発注**であり、Rejected へ丸めない（IADR-0211。S3 でも変えない）。
    [Fact]
    public async Task 接続確立の失敗は丸めずに伝播する()
    {
        var client = new FakeClient { ThrowOnPlace = () => new BrokerUnavailableException("OpenD へ接続できません") };

        var act = async () => await Adapter(client, AlternativeProtectiveOrderType.StopLimit)
            .PlaceAlternativeStopOrderAsync(CloseIntent(), 950m, 1_000m, Guid.NewGuid());

        await act.Should().ThrowAsync<BrokerUnavailableException>();
    }

    // 否定形: 送信前検証で棄却したときも、なぜ送らなかったかを理由として返す（監査に空欄を残さない）。
    [Fact]
    public async Task トレール幅が0なら送信せず棄却の理由を返す()
    {
        var client = new FakeClient();

        var placement = await Adapter(client, AlternativeProtectiveOrderType.TrailingStop)
            .PlaceAlternativeStopOrderAsync(CloseIntent(), triggerPrice: 1_000m, entryReferencePrice: 1_000m, Guid.NewGuid());

        client.LastRequest.Should().BeNull("OpenD へは送信しない");
        placement.Order.Status.Should().Be(OrderStatus.Rejected);
        placement.RejectReasonCode.Should().BeNull();
        placement.RejectReasonMessage.Should().Contain("発注前検証");
    }

    [Fact]
    public void 代替注文種別は構成で決まり発注前に読める()
    {
        Adapter(new FakeClient(), AlternativeProtectiveOrderType.TrailingStop)
            .AlternativeProtectiveOrderType.Should().Be(AlternativeProtectiveOrderType.TrailingStop);

        // 未指定（既定）は StopLimit（計画 ADR-0040 決定1 が先に挙げた種別）。
        new MoomooBrokerAdapter(new FakeClient(), BrokerProvider.MoomooSimulate)
            .AlternativeProtectiveOrderType.Should().Be(AlternativeProtectiveOrderType.StopLimit);
    }
}
