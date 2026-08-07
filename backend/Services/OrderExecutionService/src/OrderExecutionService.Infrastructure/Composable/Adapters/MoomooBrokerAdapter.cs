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
    BrokerProvider provider,
    TimeProvider? timeProvider = null,
    ILogger<MoomooBrokerAdapter>? logger = null)
    : IBrokerAdapter, IClientOrderIdBroker, IBrokerPositionSource, IBrokerAvailabilityProbe, IBrokerAccountSource
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// FR-20, FR-12, #386, IADR-0149 決定1: 本アダプタの発注先（<c>BrokerSelection.ToBrokerProvider()</c> の解決結果）。
    /// <b>既定値を与えない</b>——省略できるようにすると、書き忘れが「Stage 1 に算入される側」へ倒れる
    /// （IADR-0142 決定1 と同じ規律）。
    /// </summary>
    public BrokerProvider Provider { get; } = provider;
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

    /// <summary>
    /// FR-20, #385, 06_daytrading-review §4.2, IADR-0150: OpenD へ到達できるかを確かめる（Stage 1 の稼働監視）。
    /// <para>
    /// 建玉照会を流用するのは、それが<b>取引コンテキスト（口座・取引環境）まで通っていること</b>を
    /// 一度の往復で確かめられる既存の照会だからである。**発注は試さない**——試し発注は統制の外側で
    /// 注文を出すことであり、取引ガード（FR-19）の意味を壊す。
    /// </para>
    /// <para>
    /// <see cref="GetPositionsAsync"/> は照会不能を null に倒す（部分列挙を返さない）ため、
    /// 「null でない＝全対応市場を成功裏に列挙できた」がそのまま到達性の判定になる。
    /// </para>
    /// </summary>
    public async Task<bool> IsOperationalAsync(CancellationToken cancellationToken = default) =>
        await GetPositionsAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// FR-19, FR-10, #375, ADR-0021 決定3, IADR-0153: 接続している口座の状態を照会する。
    /// <para>
    /// 照会失敗・種別不明はいずれも <c>null</c>（＝口座種別を確認できていない）へ倒す。受け手は新規建てを止める。
    /// <b>「不明なら信用口座」へは絶対に倒さない。</b>
    /// </para>
    /// <para>
    /// <b>決済済み資金は供給しない（null のまま）。</b> moomoo API に該当するフィールドが存在しないことを
    /// 実測済みである（<c>TrdCommon.Funds</c> の全 42 プロパティ・アセンブリ全体の走査。IADR-0153 決定4）。
    /// 供給が無い以上、現金口座では買付が止まる（安全側）。
    /// </para>
    /// <para>
    /// <b>推定値・代替値で埋めてはならない</b>（#425 / ADR-0025）。とりわけ「現金買付余力」は現金口座では
    /// <b>未決済の売却代金を含む</b>のが通例であり、<b>それこそが GFV を引き起こす当の資金である。
    /// これを分母に据えると GFV 回避ガードが GFV を許可する。</b> 出金可能額も別概念である。
    /// 再混入は <c>scripts/check-banned-settled-cash-sources.js</c> が機械的に止める。
    /// 導出経路（<c>TrdFlowSummary</c>）の検証は ADR-0019 の <b>PoC 項目 8</b>（期限 2026-08-31）である。
    /// </para>
    /// <para>
    /// <b>GFV 発生回数は本型に載らない</b>（#425 / ADR-0025 決定2 / IADR-0165 決定2）。ブローカーが供給できず、
    /// 計画は<b>自前で計数する</b>ことを決めた。自前計数をブローカー照会の欄へ入れると
    /// 「ブローカーの GFV カウンタの写し」と読まれるが、<b>両者が一致する保証はない</b>。
    /// </para>
    /// </summary>
    public async Task<BrokerAccountState?> GetAccountStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var accountType = await client.GetAccountTypeAsync(cancellationToken).ConfigureAwait(false);
            return accountType switch
            {
                MoomooAccountType.Cash => new BrokerAccountState(AccountType.Cash),
                MoomooAccountType.Margin => new BrokerAccountState(AccountType.Margin),
                // 種別不明（TrdAccType_Unknown・未対応の口座種別）。既定へ丸めない。
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "moomoo 口座種別の照会に失敗したため不明（null）を返します。");
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
            ProductType: ProductType.Cash, Mode: BrokerProvider.InternalPaper, Quantity: 0, Price: 0m);
}
