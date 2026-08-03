using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Adapters;

// #13, FR-05, ADR-0002, IADR-0016: moomoo ブローカアダプタ。OpenD（IMoomooTradeClient）経由で発注する。
// SIMULATE 限定（client 実装が TrdEnv_Simulate を用いる）。実弾は撃たない。判断・記録・報告のフローは
// PaperBrokerAdapter と完全に同一（不正注文・不達は終端 Rejected で返しフローを止めない）。
//
// #141, IADR-0092: IClientOrderIdBroker を実装し、発注時に DecisionId を moomoo の remark（client order id相当）へ
// 伝播する。これにより滞留 Reserved を後から DecisionId で照合できる（実照会リコンサイル）。paper は本 capability を
// 持たないため OrderExecutionService は従来経路に倒れる。
// #292, IADR-0118: IBrokerPositionSource も実装し、建玉突合へ現在建玉を供給する。照会不能は null（＝不明）に倒す。
// paper（PaperBrokerAdapter）は本ポートを実装しないため、突合の常駐は paper 構成では起動時に自己停止する。
internal sealed class MoomooBrokerAdapter(
    IMoomooTradeClient client,
    TimeProvider? timeProvider = null,
    ILogger<MoomooBrokerAdapter>? logger = null) : IBrokerAdapter, IClientOrderIdBroker, IBrokerPositionSource
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    // fail-safe で握りつぶす例外も障害切り分けのためログする（既定 NullLogger でテスト時は無害）。
    private readonly ILogger<MoomooBrokerAdapter> _logger = logger ?? NullLogger<MoomooBrokerAdapter>.Instance;

    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default) =>
        PlaceCoreAsync(intent, remark: null, cancellationToken);

    // #141, IADR-0092: DecisionId を remark として付与して発注する（滞留 Reserved の突合キー）。
    public Task<BrokerOrder> PlaceOrderAsync(
        OrderIntent intent, Guid decisionId, CancellationToken cancellationToken = default) =>
        PlaceCoreAsync(intent, remark: MoomooClientOrderId.From(decisionId), cancellationToken);

    private async Task<BrokerOrder> PlaceCoreAsync(OrderIntent intent, string? remark, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        // FR-05, #30: 実ブローカーが拒否する不正注文（数量/価格 <= 0）は送信せず終端 Rejected で返す（Paper と同一）。
        if (intent.Quantity <= 0 || intent.Price <= 0m)
            return Terminal(intent, OrderStatus.Rejected, now);

        try
        {
            // Mode=Live でも SIMULATE を用いる（本 PR は実弾を撃たない・IADR-0016）。実弾解禁は別 IADR＋明示 config。
            var request = new MoomooOrderRequest(intent.Symbol, MapMarket(intent.Market), MapSide(intent.Side),
                intent.Quantity, intent.Price, remark);
            var result = await client.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
            return ToBrokerOrder(intent, result, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // OpenD 不達・SDK 例外は終端 Rejected（フローを止めない・実弾防止の安全側）。原因はログに残す。
            _logger.LogWarning(ex, "moomoo 発注に失敗したため Rejected に倒します symbol={Symbol} qty={Qty}",
                intent.Symbol, intent.Quantity);
            return Terminal(intent, OrderStatus.Rejected, now);
        }
    }

    public async Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await client.QueryOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
            // Intent はブローカ照会では復元できないため、状態のみを持つ最小 BrokerOrder を返す（呼び出し側は状態を用いる）。
            return result is null ? null : new BrokerOrder(result.OrderId, MinimalIntent(), MapState(result.State),
                result.FilledQuantity, result.AveragePrice, PlacedAt: default, CompletedAt: IsTerminal(result.State) ? _time.GetUtcNow() : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "moomoo 状態照会に失敗したため null を返します orderId={OrderId}", orderId);
            return null;
        }
    }

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        client.CancelOrderAsync(orderId, cancellationToken);

    // #292, IADR-0118: 現在建玉の照会。失敗は **null（不明）** に倒す。空列（建玉ゼロ）と取り違えると
    // 台帳の全建玉が乖離として報告されるため、この区別が本メソッドの中核である。
    public async Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var positions = await client.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
            return positions
                .Select(p => new BrokerPositionSnapshot(p.Symbol, MapMarketBack(p.Market), p.Quantity, p.AverageCost))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "moomoo 建玉照会に失敗したため不明（null）を返します。");
            return null;
        }
    }

    internal static Market MapMarketBack(MoomooMarket market) => market switch
    {
        MoomooMarket.Japan => Market.Japan,
        _ => Market.UnitedStates,
    };

    private BrokerOrder ToBrokerOrder(OrderIntent intent, MoomooOrderResult result, DateTimeOffset now) =>
        new(result.OrderId, intent, MapState(result.State), result.FilledQuantity, result.AveragePrice,
            PlacedAt: now, CompletedAt: IsTerminal(result.State) ? now : null);

    private static BrokerOrder Terminal(OrderIntent intent, OrderStatus status, DateTimeOffset now) =>
        new(OrderId: Guid.NewGuid().ToString("N"), Intent: intent, Status: status,
            FilledQuantity: 0, AveragePrice: 0m, PlacedAt: now, CompletedAt: now);

    internal static MoomooMarket MapMarket(Market market) => market switch
    {
        Market.Japan => MoomooMarket.Japan,
        Market.UnitedStates => MoomooMarket.UnitedStates,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "未対応の市場です。"),
    };

    internal static MoomooSide MapSide(TradeSide side) => side switch
    {
        TradeSide.Buy => MoomooSide.Buy,
        TradeSide.Sell => MoomooSide.Sell,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "未対応の売買方向です。"),
    };

    // moomoo 注文状態 → OrderStatus（安全側: 不明/失敗は Rejected）。
    internal static OrderStatus MapState(MoomooOrderState state) => state switch
    {
        MoomooOrderState.Submitting or MoomooOrderState.Submitted => OrderStatus.Accepted,
        MoomooOrderState.Filling or MoomooOrderState.FilledPart => OrderStatus.PartiallyFilled,
        MoomooOrderState.FilledAll => OrderStatus.Filled,
        MoomooOrderState.Cancelled => OrderStatus.Cancelled,
        _ => OrderStatus.Rejected,
    };

    private static bool IsTerminal(MoomooOrderState state) =>
        state is MoomooOrderState.FilledAll or MoomooOrderState.Cancelled or MoomooOrderState.Failed;

    // GetOrderAsync でブローカ照会結果に Intent が無い場合の最小プレースホルダ（状態照会用途）。
    private static OrderIntent MinimalIntent() =>
        new(Symbol: string.Empty, Market: Market.UnitedStates, Side: TradeSide.Buy,
            ProductType: ProductType.Cash, Mode: TradeMode.Paper, Quantity: 0, Price: 0m);
}
