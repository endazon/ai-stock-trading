using OrderExecutionService.Features.OrderExecution;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// #13, FR-05, ADR-0002, IADR-0016: moomoo ブローカアダプタ。OpenD（IMoomooTradeClient）経由で発注する。
// SIMULATE 限定（client 実装が TrdEnv_Simulate を用いる）。実弾は撃たない。判断・記録・報告のフローは
// PaperBrokerAdapter と完全に同一（**送信していないと言い切れる不正注文**は終端 Rejected で返しフローを止めない）。
// 🔴 FR-10, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）: **送信後に結果を確認できなかった失敗は
// Rejected へ畳まない**（BrokerDispatchIndeterminateException で伝播する。下の PlaceWithRejectionDetailAsync）。
//
// #141, IADR-0092: IClientOrderIdBroker を実装し、発注時に DecisionId を moomoo の remark（client order id相当）へ
// 伝播する。これにより滞留 Reserved を後から DecisionId で照合できる（実照会リコンサイル）。paper は本 capability を
// 持たないため OrderExecutionService は従来経路に倒れる。
// #292, IADR-0118: IBrokerPositionSource も実装し、建玉突合へ現在建玉を供給する。照会不能は null（＝不明）に倒す。
// paper（PaperBrokerAdapter）は本ポートを実装しないため、突合の常駐は paper 構成では起動時に自己停止する。
// FR-10, #331, IADR-0210: IProtectiveOrderBroker（保護逆指値・成行手仕舞い）も実装する。損切りはブローカー側
// 逆指値へ一本化されており、逆指値レグは OrderType_Stop（AuxPrice=発火価格）で発注する。
// FR-05, #331, IADR-0211: 接続確立の失敗（BrokerUnavailableException＝確実に未発注）は Rejected へ**丸めない**
// （「拒否＝証券会社が受理しなかった状態」の集計を接続障害で汚染しない）。呼び出し側が「見送り」にする。
// FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: IAlternativeProtectiveOrderBroker も実装する。
// SIMULATE が OrderType_Stop を受け付けない（#809 で実測）ため、S3 は隣の種別（StopLimit / TrailingStop。
// alternativeStop で選ぶ）を試し、**拒否理由（retType / retMsg）を戻り値へ載せて**呼び出し側の監査へ運ぶ。
public sealed class MoomooBrokerAdapter(
    IMoomooTradeClient client,
    BrokerProvider provider,
    TimeProvider? timeProvider = null,
    ILogger<MoomooBrokerAdapter>? logger = null,
    MoomooAlternativeStopSettings? alternativeStop = null)
    : IBrokerAdapter, IClientOrderIdBroker, IBrokerPositionSource, IBrokerAvailabilityProbe, IBrokerAccountSource,
      IProtectiveOrderBroker, IAlternativeProtectiveOrderBroker
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly MoomooAlternativeStopSettings _alternativeStop = alternativeStop ?? new MoomooAlternativeStopSettings();

    /// <summary>
    /// FR-20, FR-12, #386, IADR-0149 決定1: 本アダプタの発注先（<c>BrokerSelection.ToBrokerProvider()</c> の解決結果）。
    /// <b>既定値を与えない</b>——省略できるようにすると、書き忘れが「Stage 1 に算入される側」へ倒れる
    /// （IADR-0142 決定1 と同じ規律）。
    /// </summary>
    public BrokerProvider Provider { get; } = provider;
    // fail-safe で握りつぶす例外も障害切り分けのためログする（既定 NullLogger でテスト時は無害）。
    private readonly ILogger<MoomooBrokerAdapter> _logger = logger ?? NullLogger<MoomooBrokerAdapter>.Instance;

    // #846: 指値は送信前に市場の刻みへ丸める（`PlaceCoreAsync`）。向きは**不利にならない側**（買いは切り下げ）。
    public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken cancellationToken = default) =>
        PlaceCoreAsync(intent, remark: null, cancellationToken);

    // #141, IADR-0092: DecisionId を remark として付与して発注する（滞留 Reserved の突合キー）。
    public Task<BrokerOrder> PlaceOrderAsync(
        OrderIntent intent, Guid decisionId, CancellationToken cancellationToken = default) =>
        PlaceCoreAsync(intent, remark: MoomooClientOrderId.From(decisionId), cancellationToken);

    // FR-10, #331, IADR-0210: 保護逆指値（OrderType_Stop・AuxPrice=発火価格）。DecisionId を remark へ伝播する。
    // #846: 発火価格は送信前に市場の刻みへ丸める（`PlaceCoreAsync`。**実弾でも使う経路**）。
    public Task<BrokerOrder> PlaceStopOrderAsync(
        OrderIntent closeIntent, decimal triggerPrice, Guid decisionId, CancellationToken cancellationToken = default) =>
        PlaceCoreAsync(closeIntent, MoomooClientOrderId.From(decisionId), cancellationToken,
            MoomooOrderKind.Stop, triggerPrice);

    // FR-10, #331, IADR-0210: 成行の手仕舞い（逆指値が成立しない場合の建玉解消）。
    public Task<BrokerOrder> PlaceMarketOrderAsync(
        OrderIntent closeIntent, Guid decisionId, CancellationToken cancellationToken = default) =>
        PlaceCoreAsync(closeIntent, MoomooClientOrderId.From(decisionId), cancellationToken,
            MoomooOrderKind.Market);

    // FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: 本アダプタが S3 で試す注文種別（構成で決まる）。
    public AlternativeProtectiveOrderType AlternativeProtectiveOrderType => _alternativeStop.OrderType;

    // FR-10, FR-12, ADR-0040 決定1（S3）, #821, IADR-0347: 保護レグを代替注文種別で発注し、
    // **拒否理由（retType / retMsg）を戻り値へ載せる**。拒否そのものの扱い（建玉を持たない）は呼び出し側＝S0 と同一。
    public async Task<AlternativeProtectiveOrderPlacement> PlaceAlternativeStopOrderAsync(
        OrderIntent closeIntent,
        decimal triggerPrice,
        decimal entryReferencePrice,
        Guid decisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closeIntent);
        var orderType = _alternativeStop.OrderType;
        var (kind, price, trailValue) = BuildAlternativeParameters(closeIntent, triggerPrice, entryReferencePrice);
        // #844: 発火価格（AuxPrice）も刻みへ丸めて送る。指値だけ丸めても、こちらが刻みを外れていれば同じ拒否になる。
        var roundedTrigger = MoomooPriceRounding.RoundTrigger(closeIntent.Market, closeIntent.Side, triggerPrice);

        var placement = await PlaceWithRejectionDetailAsync(
                closeIntent with { Price = price },
                MoomooClientOrderId.From(decisionId),
                cancellationToken,
                kind,
                kind == MoomooOrderKind.StopLimit ? roundedTrigger : null,
                trailValue)
            .ConfigureAwait(false);

        return new AlternativeProtectiveOrderPlacement(
            placement.Order, orderType, placement.RejectReasonCode, placement.RejectReasonMessage);
    }

    // #821, IADR-0347: 代替注文種別ごとの送信パラメータ。
    // StopLimit: 指値は発火価格から**不利側**（売りなら下・買いなら上）へ StopLimitOffsetRatio だけずらす
    //   ——発火価格と同値にすると急落・急騰時に約定せず、保護レグの体を成さない。
    // TrailingStop: トレール幅は |エントリーの判断価格 − 発火価格|（発火価格そのものは送らない）。
    private (MoomooOrderKind Kind, decimal Price, decimal? TrailValue) BuildAlternativeParameters(
        OrderIntent closeIntent, decimal triggerPrice, decimal entryReferencePrice)
    {
        // 🔴 #844: ブローカーは価格の刻みを検査する。丸めずに送ると
        // `retType=-1 The precision of Price in Place Order does not meet the specification.` で拒否される
        //（稼働環境で実測。332.35 × 0.99 = 329.0265 の 4 桁が原因だった）。
        if (_alternativeStop.OrderType == AlternativeProtectiveOrderType.TrailingStop)
        {
            return (MoomooOrderKind.TrailingStop,
                MoomooPriceRounding.RoundLimit(
                    closeIntent.Market, closeIntent.Side, closeIntent.Price, triggerPrice),
                MoomooPriceRounding.RoundTrail(
                    closeIntent.Market, triggerPrice, Math.Abs(entryReferencePrice - triggerPrice)));
        }

        var offset = triggerPrice * _alternativeStop.StopLimitOffsetRatio;
        var raw = closeIntent.Side == TradeSide.Sell ? triggerPrice - offset : triggerPrice + offset;
        var limitPrice = MoomooPriceRounding.EnsureBeyondTrigger(
            closeIntent.Market,
            closeIntent.Side,
            MoomooPriceRounding.RoundLimit(closeIntent.Market, closeIntent.Side, raw, triggerPrice),
            MoomooPriceRounding.RoundTrigger(closeIntent.Market, closeIntent.Side, triggerPrice));
        return (MoomooOrderKind.StopLimit, limitPrice, null);
    }

    // FR-05, FR-10, ADR-0016, #846, IADR-0210: **送る値を市場の刻みへ丸める唯一の点**（S0・エントリー・成行）。
    // S3 は `PlaceAlternativeStopOrderAsync` が自前で丸めてから `PlaceWithRejectionDetailAsync` を直接呼ぶため
    // ここは通らない——二重に丸めて #845 が決めた S3 の向きを壊すことがない。
    //
    // 🔴 丸めた**後**の値を発注前検証へ渡す。刻みに満たない価格は 0 になり、従来どおり送信せず Rejected になる
    //（1 刻みを足して 0 を避けない。決定が求めていない価格を捏造しない＝#845 のトレール幅と同じ規律）。
    // 成行（Market）は価格も発火価格も注文へ載せないため、丸める値が無い。
    private async Task<BrokerOrder> PlaceCoreAsync(
        OrderIntent intent,
        string? remark,
        CancellationToken cancellationToken,
        MoomooOrderKind kind = MoomooOrderKind.Limit,
        decimal? triggerPrice = null)
    {
        // エントリー（および指値の決済）は**不利にならない側**＝買いは切り下げ・売りは切り上げ。
        var sentIntent = kind == MoomooOrderKind.Limit && intent.Price > 0m
            ? intent with { Price = MoomooPriceRounding.RoundEntryLimit(intent.Market, intent.Side, intent.Price) }
            : intent;
        // S0 の発火価格（AuxPrice）は**早く発火する側**＝保護が緩む側へ倒さない（#845 の S3 と同じ向き）。
        var sentTrigger = kind == MoomooOrderKind.Stop && triggerPrice is { } trigger and > 0m
            ? MoomooPriceRounding.RoundTrigger(intent.Market, intent.Side, trigger)
            : triggerPrice;

        return (await PlaceWithRejectionDetailAsync(sentIntent, remark, cancellationToken, kind, sentTrigger)
            .ConfigureAwait(false)).Order;
    }

    // #821, IADR-0347: 発注 1 回と、拒否だったときの理由。理由は S3（代替注文種別）だけが読み出す
    // ——従来経路（PlaceCoreAsync）は Order だけを取り出すため、挙動は 1 バイトも変わらない。
    private async Task<(BrokerOrder Order, int? RejectReasonCode, string? RejectReasonMessage)>
        PlaceWithRejectionDetailAsync(
            OrderIntent intent,
            string? remark,
            CancellationToken cancellationToken,
            MoomooOrderKind kind,
            decimal? triggerPrice,
            decimal? trailValue = null)
    {
        var now = _time.GetUtcNow();

        // FR-05, #30: 実ブローカーが拒否する不正注文（数量/価格 <= 0）は送信せず終端 Rejected で返す（Paper と同一）。
        // 逆指値は発火価格が正であることも要する（#331）。成行は価格を送らないため参照価格の正負は問わない。
        // #821: StopLimit は発火価格と指値の両方、TrailingStop はトレール幅が正であることを要する。
        var invalid = intent.Quantity <= 0
            || (kind is MoomooOrderKind.Limit or MoomooOrderKind.StopLimit && intent.Price <= 0m)
            || (kind is MoomooOrderKind.Stop or MoomooOrderKind.StopLimit && triggerPrice is not > 0m)
            || (kind == MoomooOrderKind.TrailingStop && trailValue is not > 0m);
        if (invalid)
        {
            return (Terminal(intent, OrderStatus.Rejected, now), null,
                $"発注前検証で棄却しました（種別={kind} 数量={intent.Quantity} 価格={intent.Price} "
                + $"発火価格={triggerPrice} トレール幅={trailValue}）。OpenD へは送信していません。");
        }

        try
        {
            // Mode=Live でも SIMULATE を用いる（本 PR は実弾を撃たない・IADR-0016）。実弾解禁は別 IADR＋明示 config。
            var request = new MoomooOrderRequest(intent.Symbol, MapMarket(intent.Market), MapSide(intent.Side),
                intent.Quantity, intent.Price, remark, kind, triggerPrice, trailValue);
            var result = await client.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
            return (ToBrokerOrder(intent, result, now), null, null);
        }
        catch (MoomooTradeRequestException ex) when (ex.IsConfirmedFailure)
        {
            // #821, IADR-0347: OpenD が**返事として**失敗を返した＝**証券会社が受理しなかった**。retType / retMsg を保って返す
            // （S3 はこの理由を監査台帳へ残すことが目的そのものである）。倒し先は従来どおり終端 Rejected。
            // 🔴 #848, IADR-0117（2026-09-19 追記・改定 8）: ここへ入れてよいのは **retType == -1（Failed）だけ**である。
            // -100（TimeOut）/ -200 / -400 / -500（Invalid）と未定義の値は「返事を読めなかった」であり
            //（-100 と -500 は SDK がクライアント側で合成する。MoomooRetType の注釈）、下の「届いたか不明」へ落とす。
            // 実測済みの拒否（#844 の価格精度・#809 の Stop 非対応）はどちらも -1 で、従来どおりここへ入る。
            _logger.LogWarning(ex,
                "moomoo 発注を拒否されました symbol={Symbol} qty={Qty} 種別={Kind} retType={RetType} retMsg={RetMsg}",
                intent.Symbol, intent.Quantity, kind, ex.RetType, ex.RetMsg);
            return (Terminal(intent, OrderStatus.Rejected, now), ex.RetType, ex.RetMsg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BrokerUnavailableException)
        {
            // 🔴 FR-05, FR-10, FR-11, UC-06, #848, IADR-0117（2026-09-19 追記・改定 6）:
            // **送信後の SDK 例外・応答異常は「届いたか不明」であり、終端 Rejected へ畳まない。**
            // ここへ落ちる代表例は返信待ちのタイムアウトで、**注文は既に送信済み**である
            //（MMApiMoomooTradeClient の分類もそう書いている）。タイムアウトは 2 つの形で来る——SDK の 12 秒打ち切りが
            // 応答の形で返す **retType=-100**（既定構成ではこちらが先。改定 8）と、SendAsync の TimeoutException。
            // **確認できた失敗（retType=-1）以外の MoomooTradeRequestException もここへ落ちる**（上の when）。
            // Rejected はリスク管理の取引台帳で
            // **在庫の押さえを解く引き金**であり、不明のまま解くと同じ建玉に 2 本目の決済が並ぶ
            //（二重決済で意図しないショート化）。エントリーでは「建玉は生じていない」という仮定になり、
            // 注文が生きていた場合に保護レグ無しの建玉ができる。実在しない注文 ID も捏造しない（#842 と同型）。
            // 不明は伝播させ、呼び出し側は**予約（IADR-0057）を解放も確定もしない**（撃ち直さない）。
            // 滞留の解消はリコンサイル（IADR-0092）が行う。#856, IADR-0362: アプリ既定は無効のままだが配備では
            // 有効であり、解放（NotPlaced）の門だけが閉じている＝自動で片付くのは「発注済み」と確定した側だけである。
            // BrokerUnavailableException（接続確立の失敗＝確実に未発注）は従来どおり丸めずに伝播する（IADR-0211）。
            _logger.LogError(ex,
                "moomoo 発注の結果を確認できませんでした（送信済み・届いたか不明）symbol={Symbol} qty={Qty} 種別={Kind}。"
                + "拒否へ畳まず、予約を Reserved のまま残します（自動リコンサイルが確定できなければ人手で確認してください）。",
                intent.Symbol, intent.Quantity, kind);
            throw new BrokerDispatchIndeterminateException(
                $"moomoo へ発注を送信しましたが結果を確認できませんでした（種別={kind} 銘柄={intent.Symbol} "
                + $"数量={intent.Quantity}）: {ex.Message}", ex);
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
    /// <b>口座の評価額（<c>EquityInBase</c>）は供給する</b>（#869 / ADR-0041 決定2 / IADR-0354）。
    /// <c>TrdGetFunds</c> の <c>Funds.TotalAssets</c>（資産純値・USD）であり、<b>決済済み資金とは別のフィールドで実在する</b>。
    /// 取れなければ <c>null</c> のままとし（統制の基準資金が未供給＝新規建てが止まる側）、買付余力で代替しない。
    /// <b>応答が USD 以外の通貨を<u>明示</u>したときも <c>null</c> である。</b>
    /// 🔴 <b>［2026-09-19 / #897］通貨の欄が<u>無い</u>応答は、要求した通貨（USD）を前提として採る（近似）。</b>
    /// <c>Funds.currency</c> は protobuf の optional であり、<b>本系の口座に対して載らない</b> ——
    /// 「名乗っているときだけ採る」（#874）は実機で常時 fail-closed になり、基準資金が一度も供給されなかった。
    /// 🔴 <b>欄の有無は OpenD の版ではなく口座種別で決まる</b>（要求した通貨は universal 証券口座・先物口座に
    /// しか効かず、単一市場口座では無視される）。<b>近似の根拠は「本系の照会先が US 単一市場口座である」
    /// という #397 の実機実測</b>であって、要求が尊重されるという契約ではない。
    /// 非 USD の単一市場口座を足すと破れる（残余リスク。IADR-0354 §結果 / #899）。
    /// 🔴 <b>［2026-09-23 / #899 / IADR-0373］その近似の範囲を「反証」で狭めた。</b> 通貨の欄が無い応答でも、
    /// 現金の内訳（<c>Funds.cashInfoList</c>）が<b>通貨を名乗る行を持ち、そのどれも USD でない</b>なら採らない。
    /// <b>確証（USD だと確かめられたときだけ採る）ではない</b> —— 欄が無い・行が通貨を名乗らないときは
    /// 何もしない（従来どおり採る）ため、<b>新たな fail-closed を生まない</b>。
    /// 反証は<b>通貨を明示しない経路にだけ</b>効かせる（明示された通貨を内訳で上書きしない）。
    /// </para>
    /// <para>
    /// 🔴 <b>「応答に評価額が無い」場合に限り、口座種別は残す。</b> 欄が別だからである。
    /// <b>評価額の照会が例外で終わった場合（OpenD 不達・応答異常）は、下の <c>catch</c> が口座照会全体の失敗として
    /// <c>null</c> を返し、口座種別も一緒に落ちる</b>（fail-closed。<c>GetAccountTypeAsync</c> の失敗と同じ扱い）。
    /// 「評価額が取れなくても種別は残る」と読める広い書き方をしない——実挙動は上の 2 つで分かれる。
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
            if (accountType is not (MoomooAccountType.Cash or MoomooAccountType.Margin))
            {
                // 種別不明（TrdAccType_Unknown・未対応の口座種別）。既定へ丸めない。
                return null;
            }

            // FR-10, #869, ADR-0041 決定2, IADR-0354: 同じ照会で口座の評価額（基準資金の供給元）も取る。
            // **応答に評価額が無い（null）ことを理由に口座種別まで捨てない**——種別は種別で確認できており、
            // 捨てると口座種別依存の統制（ADR-0021 決定4）まで一緒に沈黙する。載せる欄を分けてある。
            // 🔴 **例外で終わった場合は別である**——下の catch が口座照会全体の失敗として null を返し、
            // 口座種別も一緒に落ちる（fail-closed。GetAccountTypeAsync の失敗と同じ扱い）。
            var equityInBase = await client.GetAccountEquityInBaseAsync(cancellationToken).ConfigureAwait(false);

            return new BrokerAccountState(
                accountType == MoomooAccountType.Cash ? AccountType.Cash : AccountType.Margin,
                EquityInBase: equityInBase);
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

    // moomoo 注文状態 → OrderStatus。
    //
    // 🔴 FR-10, UC-06, #848, IADR-0117（2026-09-19 追記・改定 3）: **安全側は「不明を終端にしないこと」である。**
    // 旧コメントは「安全側: 不明/失敗は Rejected」と書いていたが、これは事実と食い違っていた ——
    // リスク管理の取引台帳が Rejected を**在庫解放の引き金**にした時点で、不明を Rejected へ畳むことは
    // 「状態が分からないまま建玉の押さえを解く」（＝二重決済で意図しないショート化）になった。
    // 確認できた失敗（Failed）だけを Rejected とし、不明（Unknown）と**名前を付けられない状態（既定）**は
    // 非終端（Accepted）へ倒す。約定追跡（OrderFillPoller）が非終端を引き直し続け、本当の状態へ解決する。
    public static OrderStatus MapState(MoomooOrderState state) => state switch
    {
        MoomooOrderState.Submitting or MoomooOrderState.Submitted => OrderStatus.Accepted,
        MoomooOrderState.Filling or MoomooOrderState.FilledPart => OrderStatus.PartiallyFilled,
        MoomooOrderState.FilledAll => OrderStatus.Filled,
        MoomooOrderState.Cancelled => OrderStatus.Cancelled,
        // 証券会社が受理しなかったことが**分かっている**状態。在庫解放の対象のままにする
        //（発注拒否で押さえが解けるのは #848 の射程内であり、外すと 2 つ目の恒久ロックを作る）。
        MoomooOrderState.Failed => OrderStatus.Rejected,
        // 🔴 この既定アームは **Unknown 専用ではない**。いま到達するのは MoomooOrderState.Unknown だけだが、
        // 将来 MoomooOrderState へ値を足して上のアームへ写し忘れた場合も**ここへ落ちる**（＝非終端）。
        // 向きは意図どおり（名前を付けられない状態で在庫を解放しない）。ただし新しい値が**終端**を意味するなら
        // 必ず明示のアームを足すこと——足し忘れると終端が届かず、約定追跡が引き直し続ける（安全側だが解けない）。
        _ => OrderStatus.Accepted,
    };

    // #848: Unknown は**終端に入れない**（CompletedAt を立てない・約定追跡が引き直す）。
    private static bool IsTerminal(MoomooOrderState state) =>
        state is MoomooOrderState.FilledAll or MoomooOrderState.Cancelled or MoomooOrderState.Failed;

    // GetOrderAsync でブローカ照会結果に Intent が無い場合の最小プレースホルダ（状態照会用途）。
    private static OrderIntent MinimalIntent() =>
        new(Symbol: string.Empty, Market: Market.UnitedStates, Side: TradeSide.Buy,
            ProductType: ProductType.Cash, Mode: BrokerProvider.InternalPaper, Quantity: 0, Price: 0m);
}
