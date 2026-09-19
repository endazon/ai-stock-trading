using System.Collections.Concurrent;
using System.Linq;
using AiStockTrading.Shared.Contracts.Ports;
using Microsoft.Extensions.Logging;
using Moomoo.OpenApi;
using Moomoo.OpenApi.Pb;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// #13, FR-05, ADR-0002, IADR-0016: moomoo-api（MMAPI4Net）による実 OpenD 結合。
// SIMULATE 限定（TrdEnv_Simulate=0）で発注する。実弾（TrdEnv_Real）は撃たない。
//
// OpenD（常駐・#124）へ TCP protobuf で接続し、非同期コールバック（nSerialNo 相関）で応答を待つ。
// MMSPI_Conn（接続）と MMSPI_Trd（取引・全 OnReply_* 実装が必要）の両インターフェースを実装する。
// 未使用のコールバックは no-op。接続/口座取得は初回利用時に遅延実行する（起動をブロックしない）。
public sealed class MMApiMoomooTradeClient : MMSPI_Trd, MMSPI_Conn, IMoomooTradeClient, IDisposable
{
    private static readonly object InitGate = new();
    private static bool _apiInitialized;

    private readonly MoomooBrokerOptions _options;
    // #132: 応答待ちは構成から外部化する（Broker:Moomoo:OpenD:ReplyTimeoutSeconds・既定 15 秒＝従来のハードコード値）。
    private readonly TimeSpan _replyTimeout;
    private readonly ILogger<MMApiMoomooTradeClient> _logger;
    // #732, IADR-0327: 接続オブジェクトは**作り直せる**必要がある（readonly にしない）。一度 Connection refused を
    // 受けた MMAPI_Trd は、以後 InitConnect を呼んでも TCP を張り直さない（true を返すだけ）。
    private readonly IMoomooTradeConnectionFactory _connectionFactory;
    // 差し替えは _connectGate の内側だけで起きるが、読み手はその外側（送信側）にもいる。
    // 差し替えを読み手へ確実に見せるため volatile とし、1 回の操作の中では**必ずローカルへ受けてから使う**
    // （途中で別インスタンスへ移らないようにする）。
    private volatile IMoomooTradeConnection _connection;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<object>> _pending = new();
    private readonly object _sendGate = new(); // serial 採番＋登録とコールバック完了の相互排他（レース防止）。
    // 照会/取消は市場（TrdMarket/TrdSecMarket）を要するため、発注時に orderId→市場を控える。
    private readonly ConcurrentDictionary<string, (int TrdMarket, int SecMarket)> _orderMarket = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private TaskCompletionSource<long>? _connectTcs;
    private volatile bool _connected;
    // #732: 直前の接続試行が失敗した／切断された＝次の InitConnect の前に接続オブジェクトを作り直す。
    private volatile bool _connectionStale;
    // #732, FR-11: 通算の作り直し回数。固着（作り直しに入っていない）と不達（作り直しても繋がらない）を
    // ログだけで切り分けられるようにするための目印。
    private int _recreateCount;
    private readonly bool _encrypt;
    // #732: RSA 秘密鍵（PKCS#1 PEM の内容）。作り直しのたびに再適用するため保持する。**ログへ出さない。**
    private readonly string? _rsaPrivateKeyPem;
    private ulong _simAccId;
    // #375, ADR-0021 決定3: 接続時に確定する SIMULATE 口座の種別（TrdAcc.AccType の写像）。
    // **不明（TrdAccType_Unknown・未対応値）は null のまま**であり、「信用口座とみなす」に倒さない。
    private MoomooAccountType? _simAccType;
    private bool _disposed;

    // #732, IADR-0327: connectionFactory は接続オブジェクトの生成点。既定は本番の SDK 実装であり、
    // Program.cs の登録（2 引数）は変更していない。テストはここへフェイクを差す。
    public MMApiMoomooTradeClient(
        MoomooBrokerOptions options,
        ILogger<MMApiMoomooTradeClient> logger,
        IMoomooTradeConnectionFactory? connectionFactory = null)
    {
        // #132, IADR-0060: 構成ミス（RSA 鍵の未マウント等）は「接続はするが trade だけ落ちる」ではなく起動時に落とす。
        MoomooPreflight.Validate(options, File.Exists);
        _options = options;
        _replyTimeout = options.ReplyTimeout;
        _logger = logger;
        _connectionFactory = connectionFactory ?? new MMApiTradeConnectionFactory();
        lock (InitGate)
        {
            if (!_apiInitialized)
            {
                MMAPI.Init();
                _apiInitialized = true;
            }
        }
        // moomoo は cross-network の trade 接続に暗号化を要求する。RSA 秘密鍵が構成されていれば暗号化で接続する。
        // SetRsaPrivateKey は鍵の内容（PKCS#1 PEM 文字列）を受け取る（パスではない）。
        // 鍵パスが構成済みなら存在は preflight が保証済み（不在なら上で停止している）。同一コンストラクタ内で
        // 直後に読むため TOCTOU は問題にならない。ここを非同期化・遅延化するなら読み取り失敗の扱いを足すこと。
        // #732: 読むのはここ 1 度きりで、接続オブジェクトを作り直すたびに保持した内容を再適用する
        // （作り直しのたびにファイルを読み直すと、鍵の差し替え中に失敗する経路が増える）。
        if (!string.IsNullOrWhiteSpace(options.RsaPrivateKeyPath))
        {
            _rsaPrivateKeyPem = File.ReadAllText(options.RsaPrivateKeyPath);
            _encrypt = true;
        }
        _connection = CreateConfiguredConnection();
    }

    // 接続オブジェクトを 1 つ作り、コールバックと鍵を配線して返す。**状態は持たせない。**
    private IMoomooTradeConnection CreateConfiguredConnection()
    {
        var connection = _connectionFactory.Create();
        connection.SetClientInfo("ai-stock-trading", 1);
        connection.SetConnCallback(this);
        connection.SetTrdCallback(this);
        if (_rsaPrivateKeyPem is not null)
        {
            connection.SetRsaPrivateKey(_rsaPrivateKeyPem);
        }
        return connection;
    }

    // ---- IMoomooTradeClient ----

    public async Task<MoomooOrderResult> PlaceOrderAsync(MoomooOrderRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        // #732: 1 操作の中で接続オブジェクトが別インスタンスへ移らないよう、ここで受けて以降は local を使う。
        var connection = _connection;
        var (trdMarket, secMarket) = MapMarket(request.Market);
        var side = request.Side == MoomooSide.Sell ? TrdCommon.TrdSide.TrdSide_Sell : TrdCommon.TrdSide.TrdSide_Buy;

        // FR-10, #331, IADR-0210: 注文種別の写像。Limit=指値（従来）／Stop=逆指値（発火価格は AuxPrice）／
        // Market=成行。Stop・Market には Price を載せない（発火後成行・板成行の意味を保つ）。
        // FR-10, #821, IADR-0347: S3 の代替種別。StopLimit=発火価格（AuxPrice）＋**指値**（Price）、
        // TrailingStop=トレール幅の絶対額（TrailType_Amount・TrailValue。発火後は成行のため TrailSpread=0）。
        var orderType = request.Kind switch
        {
            MoomooOrderKind.Stop => TrdCommon.OrderType.OrderType_Stop,
            MoomooOrderKind.Market => TrdCommon.OrderType.OrderType_Market,
            MoomooOrderKind.StopLimit => TrdCommon.OrderType.OrderType_StopLimit,
            MoomooOrderKind.TrailingStop => TrdCommon.OrderType.OrderType_TrailingStop,
            _ => TrdCommon.OrderType.OrderType_Normal,
        };
        var c2sBuilder = TrdPlaceOrder.C2S.CreateBuilder()
            .SetPacketID(connection.NextPacketId()) // 発注は packetID（冪等キー）必須
            .SetHeader(BuildHeader(trdMarket))
            .SetTrdSide((int)side)
            .SetOrderType((int)orderType)
            .SetCode(request.Symbol)
            .SetQty(request.Quantity)
            .SetSecMarket(secMarket);
        if (request.Kind is MoomooOrderKind.Limit or MoomooOrderKind.StopLimit)
            c2sBuilder.SetPrice((double)request.Price);
        if (request.Kind is MoomooOrderKind.Stop or MoomooOrderKind.StopLimit && request.TriggerPrice is { } trigger)
            c2sBuilder.SetAuxPrice((double)trigger);
        // #821, IADR-0347: トレーリングストップは「幅」で指定する（発火価格を持たない）。
        if (request.Kind == MoomooOrderKind.TrailingStop && request.TrailValue is { } trail)
        {
            c2sBuilder.SetTrailType((int)TrdCommon.TrailType.TrailType_Amount);
            c2sBuilder.SetTrailValue((double)trail);
            c2sBuilder.SetTrailSpread(0d); // 発火後は成行（TrailingStopLimit ではない）ため価差は置かない。
        }
        // #141, IADR-0092: DecisionId を remark（client order id相当）として紐づける。滞留 Reserved を後から
        // DecisionId で照合し、実照会リコンサイルで発注済みを終端化・未発注を解放できるようにする。
        if (!string.IsNullOrEmpty(request.Remark))
            c2sBuilder.SetRemark(request.Remark);
        var req = TrdPlaceOrder.Request.CreateBuilder().SetC2S(c2sBuilder.Build()).Build();

        var rsp = (TrdPlaceOrder.Response)await SendAsync(() => connection.PlaceOrder(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "PlaceOrder");

        var orderId = rsp.S2C.OrderID.ToString();
        _orderMarket[orderId] = (trdMarket, secMarket);
        _logger.LogInformation("moomoo SIMULATE 発注成功 orderId={OrderId} {Side} {Symbol} x{Qty}@{Price}",
            orderId, side, request.Symbol, request.Quantity, request.Price);
        // 発注直後は約定前。状態追跡は QueryOrderAsync（GetOrderList）で行う。
        return new MoomooOrderResult(orderId, MoomooOrderState.Submitted, 0, 0m);
    }

    public async Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (!ulong.TryParse(orderId, out var oid))
        {
            return null;
        }
        // 発注時に市場を控えているが、再起動でキャッシュを失っても US 固定に倒さず対応市場を順に照会する。
        foreach (var trdMarket in MarketsToTry(orderId))
        {
            var order = await FindOrderAsync(oid, trdMarket, cancellationToken).ConfigureAwait(false);
            if (order is not null)
            {
                return new MoomooOrderResult(orderId, MapState(order.OrderStatus), (int)order.FillQty, (decimal)order.FillAvgPrice);
            }
        }
        return null;
    }

    // #141, IADR-0092: 発注時に付与した remark（clientOrderId＝DecisionId）で滞留 Reserved を照合する。
    // SIMULATE 口座の全対応市場について「現在（当日 GetOrderList）」→「履歴（GetHistoryOrderList・ReservedAt を覆う窓）」の
    // 順に走査し、remark 一致注文を返す。全て成功裏に列挙して一致ゼロなら null（＝確実に未発注）。
    //
    // fail-safe の要: いずれかの照会が失敗（EnsureSucceeded が投げる／タイムアウト）すれば例外がそのまま伝播し、
    // 呼び出し側（MoomooReservationBrokerProbe）が Indeterminate に倒す。「不明」を null と取り違えないこと。
    public async Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
        string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientOrderId))
        {
            // remark 無し（伝播前の注文等）は DecisionId で照合不能。誤って NotPlaced（=null）に倒さず「不明」を送出する。
            throw new InvalidOperationException("clientOrderId（remark）が空です。remark 照合による確実な判定はできません。");
        }
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        // 履歴窓は ReservedAt を確実に覆うよう広めに取る（境界・タイムゾーンのずれを margin で吸収）。
        var endUtc = DateTimeOffset.UtcNow.AddDays(1);
        var beginUtc = reservedAtUtc.AddDays(-2);

        foreach (var (trdMarket, _) in SupportedMarkets)
        {
            var current = await FindByRemarkInCurrentAsync(clientOrderId, trdMarket, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                return current;
            }
            var history = await FindByRemarkInHistoryAsync(clientOrderId, trdMarket, beginUtc, endUtc, cancellationToken)
                .ConfigureAwait(false);
            if (history is not null)
            {
                return history;
            }
        }
        // 全市場・現在＋履歴を成功裏に列挙して一致ゼロ＝確実に未発注。
        return null;
    }

    // #292, IADR-0118: SIMULATE 口座の現在建玉を全対応市場について列挙する。
    // いずれかの市場で失敗すれば EnsureSucceeded／タイムアウトの例外がそのまま伝播する（部分列挙を返さない）。
    // #827: 応答の連結はしない。行は照会ヘッダの市場と組にして CollectPositions へ渡す（二重計上の是正はそちら）。
    public async Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<MoomooPositionRow>();
        foreach (var (trdMarket, _) in SupportedMarkets)
        {
            var c2s = TrdGetPositionList.C2S.CreateBuilder()
                .SetHeader(BuildHeader(trdMarket))
                .SetRefreshCache(true)
                .Build();
            var req = TrdGetPositionList.Request.CreateBuilder().SetC2S(c2s).Build();
            var rsp = (TrdGetPositionList.Response)await SendAsync(() => _connection.GetPositionList(req), cancellationToken)
                .ConfigureAwait(false);
            EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetPositionList");

            foreach (TrdCommon.Position p in rsp.S2C.PositionListList)
                rows.Add(ToPositionRow(trdMarket, p));
        }

        var positions = CollectPositions(rows);

        // #827: 配備後に live SIMULATE の応答形（行の TrdMarket・PositionID の有無）を 1 回の観測で確かめるための要約。
        // 件数と市場値の分布だけを出す（銘柄・数量・口座番号は出さない）。
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "建玉照会の応答要約 rows={Rows} byQueriedMarket={ByQueriedMarket} byRowMarket={ByRowMarket} distinctCodes={DistinctCodes} withPositionId={WithPositionId} collected={Collected}",
                rows.Count,
                string.Join(",", rows.GroupBy(r => r.QueriedTrdMarket).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")),
                string.Join(",", rows.GroupBy(r => r.PositionTrdMarket?.ToString() ?? "unset").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}")),
                rows.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count(),
                rows.Count(r => r.PositionId is not null),
                positions.Count);
        }

        return positions;
    }

    // moomoo Position → SDK 非依存の行。写像は protobuf 依存のため live 検証に委ねる（組み立ては照会経路テストで固定）。
    // 行の市場が未設定なら null（＝不明）。「不明」を照会ヘッダの市場で埋めない（#827 決定 1）。
    // PositionID も未設定なら null（重複排除は (市場, 銘柄, 方向) へ退避する。#827 決定 2）。
    private static MoomooPositionRow ToPositionRow(int queriedTrdMarket, TrdCommon.Position p) =>
        new(
            QueriedTrdMarket: queriedTrdMarket,
            PositionTrdMarket: p.HasTrdMarket ? p.TrdMarket : null,
            Code: p.Code,
            IsShort: p.PositionSide == (int)TrdCommon.PositionSide.PositionSide_Short,
            Quantity: (int)p.Qty,
            CostPrice: (decimal)p.CostPrice,
            PositionId: p.HasPositionID ? p.PositionID : null);

    // #827, FR-05, FR-10, IADR-0118: 市場ごとの建玉照会の応答行を 1 つの建玉一覧へ畳む（SDK 非依存・単体テスト対象）。
    //
    // SIMULATE 口座は照会ヘッダの市場を問わず同じ建玉を返す（稼働クラスタの実測: US 株 848 株が US・JP の両応答に現れ、
    // 連結すると 1696 株として乖離を誤報した）。
    //   決定 1（主）: 行の市場が**対応市場（SupportedMarkets）のいずれか**で、かつ照会ヘッダの市場と異なる行だけを捨てる。
    //                その建玉はもう一方の対応市場の照会で採られるため欠けない。
    //                それ以外（未設定・TrdMarket_Unknown・HK や *_Fund 等の対応外の市場）は捨てない——どの照会でも
    //                「照会市場と異なる」ため、捨てると実在する建玉が消え、保護逆指値ガードの残数量 0・偽の LedgerOnly へ倒れる。
    //                対応外の市場の写像は従来どおり（JP 以外は UnitedStates）で、両照会に現れても決定 2 で 1 件に畳まれる。
    //   決定 2: PositionID があれば (市場, PositionID) で、無ければ (市場, 銘柄, 方向) で最初の 1 件だけ採る。
    //           ブローカが同じ銘柄・方向を別ロット（別 PositionID）で返しても併合しない（数量の合算は下流の突合が行う）。
    // moomoo の Qty は常に非負で方向は PositionSide が持つため符号付きへ畳む（取引台帳の射影と同じ表現に揃える）。
    public static IReadOnlyList<MoomooPositionSnapshot> CollectPositions(IEnumerable<MoomooPositionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var positions = new List<MoomooPositionSnapshot>();
        var seen = new HashSet<(MoomooMarket Market, ulong? PositionId, string? Code, bool? IsShort)>();
        foreach (var row in rows)
        {
            var rowInSupportedMarket = row.PositionTrdMarket is { } m && SupportedMarkets.Any(s => s.TrdMarket == m);
            if (rowInSupportedMarket && row.PositionTrdMarket != row.QueriedTrdMarket)
                continue;

            var market = row.PositionTrdMarket == (int)TrdCommon.TrdMarket.TrdMarket_JP ? MoomooMarket.Japan : MoomooMarket.UnitedStates;
            var key = row.PositionId is { } id
                ? (market, (ulong?)id, (string?)null, (bool?)null)
                : (market, null, row.Code, row.IsShort);
            if (!seen.Add(key))
                continue;

            positions.Add(new MoomooPositionSnapshot(
                Symbol: row.Code,
                Market: market,
                Quantity: row.IsShort ? -row.Quantity : row.Quantity,
                AverageCost: row.CostPrice));
        }
        return positions;
    }

    // 当日注文（GetOrderList）から remark 一致を返す。
    private async Task<MoomooOrderSnapshot?> FindByRemarkInCurrentAsync(
        string remark, int trdMarket, CancellationToken cancellationToken)
    {
        var c2s = TrdGetOrderList.C2S.CreateBuilder()
            .SetHeader(BuildHeader(trdMarket))
            .SetRefreshCache(true)
            .Build();
        var req = TrdGetOrderList.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (TrdGetOrderList.Response)await SendAsync(() => _connection.GetOrderList(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetOrderList");
        return MatchByRemark(rsp.S2C.OrderListList, remark);
    }

    // 履歴注文（GetHistoryOrderList・時刻窓）から remark 一致を返す。
    private async Task<MoomooOrderSnapshot?> FindByRemarkInHistoryAsync(
        string remark, int trdMarket, DateTimeOffset beginUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
    {
        var filter = TrdCommon.TrdFilterConditions.CreateBuilder()
            .SetBeginTime(FormatFilterTime(beginUtc))
            .SetEndTime(FormatFilterTime(endUtc))
            .Build();
        var c2s = TrdGetHistoryOrderList.C2S.CreateBuilder()
            .SetHeader(BuildHeader(trdMarket))
            .SetFilterConditions(filter)
            .Build();
        var req = TrdGetHistoryOrderList.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (TrdGetHistoryOrderList.Response)await SendAsync(() => _connection.GetHistoryOrderList(req), cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetHistoryOrderList");
        return MatchByRemark(rsp.S2C.OrderListList, remark);
    }

    // remark 一致の探索。一致ゼロは null。
    private static MoomooOrderSnapshot? MatchByRemark(IEnumerable<TrdCommon.Order> orders, string remark)
    {
        foreach (TrdCommon.Order o in orders)
        {
            if (o.HasRemark && string.Equals(o.Remark, remark, StringComparison.Ordinal))
            {
                return ToSnapshot(o);
            }
        }
        return null;
    }

    // moomoo Order → SDK 非依存スナップショット。状態・約定はブローカ実体。時刻は create/update timestamp（無ければ null）。
    // 写像は SDK（protobuf Order）依存のため live 検証（既存 mapping テストの方針＝protobuf を組まない）に委ねる。
    private static MoomooOrderSnapshot ToSnapshot(TrdCommon.Order o)
    {
        var state = MapState(o.OrderStatus);
        var placedAt = FromUnixTimestamp(o.HasCreateTimestamp ? o.CreateTimestamp : (double?)null);
        var completedAt = IsTerminal(state)
            ? FromUnixTimestamp(o.HasUpdateTimestamp ? o.UpdateTimestamp : (double?)null)
            : null;
        return new MoomooOrderSnapshot(
            OrderId: o.OrderID.ToString(),
            State: state,
            Symbol: o.Code,
            Market: o.TrdMarket == (int)TrdCommon.TrdMarket.TrdMarket_JP ? MoomooMarket.Japan : MoomooMarket.UnitedStates,
            Side: o.TrdSide == (int)TrdCommon.TrdSide.TrdSide_Sell ? MoomooSide.Sell : MoomooSide.Buy,
            Quantity: (int)o.Qty,
            Price: (decimal)o.Price,
            FilledQuantity: (int)o.FillQty,
            AveragePrice: (decimal)o.FillAvgPrice,
            PlacedAt: placedAt,
            CompletedAt: completedAt);
    }

    private static bool IsTerminal(MoomooOrderState state) =>
        state is MoomooOrderState.FilledAll or MoomooOrderState.Cancelled or MoomooOrderState.Failed;

    private static DateTimeOffset? FromUnixTimestamp(double? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds.Value * 1000)) : null;

    // moomoo の履歴フィルタ時刻書式（"yyyy-MM-dd HH:mm:ss"）。タイムゾーンずれは呼び出し側の広い窓で吸収する。
    private static string FormatFilterTime(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public async Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (!ulong.TryParse(orderId, out var oid))
        {
            return;
        }
        // 市場を特定する（キャッシュ→無ければ照会で発見）。誤った市場での取消（サイレント失敗）を避ける。
        int? trdMarket = _orderMarket.TryGetValue(orderId, out var mk) ? mk.TrdMarket : null;
        if (trdMarket is null)
        {
            foreach (var m in SupportedMarkets.Select(x => x.TrdMarket))
            {
                if (await FindOrderAsync(oid, m, cancellationToken).ConfigureAwait(false) is not null)
                {
                    trdMarket = m;
                    break;
                }
            }
        }
        if (trdMarket is null)
        {
            return; // 注文が見つからない（既に消えた等）→ no-op
        }
        // #732: PlaceOrderAsync と同じく、採番と送信を同じ接続オブジェクトで行う。
        var connection = _connection;
        var c2s = TrdModifyOrder.C2S.CreateBuilder()
            .SetPacketID(connection.NextPacketId()) // 変更/取消も packetID 必須
            .SetHeader(BuildHeader(trdMarket.Value))
            .SetOrderID(oid)
            .SetModifyOrderOp((int)TrdCommon.ModifyOrderOp.ModifyOrderOp_Cancel)
            .Build();
        var req = TrdModifyOrder.Request.CreateBuilder().SetC2S(c2s).Build();

        var rsp = (TrdModifyOrder.Response)await SendAsync(() => connection.ModifyOrder(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "CancelOrder");
    }

    // 指定市場の注文一覧から orderId 一致を返す（見つからなければ null）。
    private async Task<TrdCommon.Order?> FindOrderAsync(ulong oid, int trdMarket, CancellationToken cancellationToken)
    {
        var c2s = TrdGetOrderList.C2S.CreateBuilder()
            .SetHeader(BuildHeader(trdMarket))
            .SetRefreshCache(true)
            .Build();
        var req = TrdGetOrderList.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (TrdGetOrderList.Response)await SendAsync(() => _connection.GetOrderList(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetOrderList");
        foreach (TrdCommon.Order o in rsp.S2C.OrderListList)
        {
            if (o.OrderID == oid)
            {
                return o;
            }
        }
        return null;
    }

    // 照会に用いる市場: キャッシュがあればそれのみ、無ければ対応市場すべて（再起動後の取りこぼし防止）。
    private IEnumerable<int> MarketsToTry(string orderId) =>
        _orderMarket.TryGetValue(orderId, out var mk)
            ? new[] { mk.TrdMarket }
            : SupportedMarkets.Select(m => m.TrdMarket);

    // ---- 接続・口座 ----

    // #331, IADR-0211: 接続確立の失敗は BrokerUnavailableException に分類する——この段階の失敗は
    // **注文がブローカーへ届き得ない**（確実に未発注）ため、発注執行は予約を解放して「見送り」にできる。
    // 発注**送信後**の失敗（SendAsync のタイムアウト等）は届いたか不明であり、本分類の対象外
    // （アダプタが BrokerDispatchIndeterminateException へ包んで伝播し、予約とリコンサイル
    // 〔IADR-0057/0092〕が守る。**拒否へ畳まない**——#848 / IADR-0117 改定 6）。
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            return;
        }
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                return;
            }
            // #732: 前回の試行が失敗した（または切断された）なら、InitConnect の前に接続オブジェクトを作り直す。
            // これをしないと、SDK が固着したまま InitConnect が true を返し続け、TCP が 1 本も張られないまま
            // 応答待ちのタイムアウトを繰り返す（＝入れ直すまで発注経路が死ぬ）。
            if (_connectionStale)
            {
                RecreateConnection();
            }
            _connectTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            _logger.LogInformation("OpenD へ接続します {Host}:{Port} encrypt={Encrypt}", _options.OpenDHost, _options.OpenDPort, _encrypt);
            if (!_connection.InitConnect(_options.OpenDHost, _options.OpenDPort, _encrypt))
            {
                throw new BrokerUnavailableException($"OpenD への InitConnect が失敗しました（{_options.OpenDHost}:{_options.OpenDPort}）。");
            }
            await _connectTcs.Task.WaitAsync(_replyTimeout, cancellationToken).ConfigureAwait(false);
            (_simAccId, _simAccType) = await FetchSimulateAccountAsync(cancellationToken).ConfigureAwait(false);
            _connected = true;
            _logger.LogInformation(
                "OpenD 接続完了・SIMULATE 口座 accId={AccId} 種別={AccType}",
                _simAccId,
                _simAccType?.ToString() ?? "不明");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BrokerUnavailableException)
        {
            // 接続応答の失敗・タイムアウト・口座列挙の失敗——いずれも注文送信前＝確実に未発注。
            throw new BrokerUnavailableException("OpenD への接続を確立できませんでした（未発注）。", ex);
        }
        finally
        {
            // #732: **接続が確立できなかった経路をここで一様に拾う。** InitConnect が false を返した経路は
            // BrokerUnavailableException を直接投げるため上の catch フィルタを通らず、キャンセルも通らない。
            // 失敗した接続オブジェクトは次の試行で作り直す。
            if (!_connected)
            {
                _connectionStale = true;
            }
            // 打ち切った試行の待ち合わせを残さない（遅れて来たコールバックは行き先を失って no-op になる）。
            _connectTcs = null;
            _connectGate.Release();
        }
    }

    // #732, FR-11, IADR-0327: 固着した接続オブジェクトを捨てて作り直す。**_connectGate の内側でのみ呼ぶ。**
    private void RecreateConnection()
    {
        var stale = _connection;
        // 先に差し替える。**新しい接続を見せてから古い方を手放す**——順序が逆だと、この瞬間に
        // 進行中の呼び出しが「解放済みの接続」を掴む窓が広がる（Close/Dispose は下で行う）。
        _connection = CreateConfiguredConnection();
        try
        {
            stale.Close();
        }
        catch (Exception ex)
        {
            // 解放に失敗しても作り直しは続ける（固着したまま使い続けるより捨てるほうが安全）。
            _logger.LogWarning(ex, "固着した OpenD 接続オブジェクトの Close で例外（解放は続行します）");
        }
        finally
        {
            // Close が投げても Dispose は必ず呼ぶ（同じ try に置くと握りっぱなしで漏れる）。
            try
            {
                stale.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "固着した OpenD 接続オブジェクトの Dispose で例外");
            }
        }
        _connectionStale = false;
        _recreateCount++;
        // 「作り直しても繋がらない（＝OpenD が本当に落ちている）」と「作り直しに入っていない（＝別の欠陥）」を
        // ログだけで切り分けられるようにする。**秘匿情報は出さない**（ホスト・ポート・回数のみ）。
        _logger.LogWarning(
            "OpenD 接続オブジェクトを作り直しました（直前の接続試行が失敗／切断されたため）。{Host}:{Port} 通算作り直し={RecreateCount}",
            _options.OpenDHost,
            _options.OpenDPort,
            _recreateCount);
    }

    private async Task<(ulong AccId, MoomooAccountType? AccType)> FetchSimulateAccountAsync(
        CancellationToken cancellationToken)
    {
        // userID は protobuf required。0 = 現在ログイン中のユーザー（全口座）。
        var c2s = TrdGetAccList.C2S.CreateBuilder().SetUserID(0).Build();
        var req = TrdGetAccList.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (TrdGetAccList.Response)await SendAsync(() => _connection.GetAccList(req), cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetAccList");

        foreach (TrdCommon.TrdAcc acc in rsp.S2C.AccListList)
        {
            if (acc.TrdEnv == (int)TrdCommon.TrdEnv.TrdEnv_Simulate)
            {
                return (acc.AccID, MapAccountType(acc.AccType));
            }
        }
        throw new InvalidOperationException("OpenD に SIMULATE 口座が見つかりません（moomoo の模擬取引口座を有効化してください）。");
    }

    // #375, ADR-0021 決定3: TrdAccType（Unknown=0 / Cash=1 / Margin=2 / TFSA / RRSP / SRRSP / Derivatives）を
    // 本システムが扱う 2 値へ写像する。
    //
    // **未知の値・Unknown は null（＝不明）へ倒す。** ADR-0021 が想定するのは信用口座と現金口座の 2 種であり
    // （決定2「同時に有効なのは 1 種別」）、TFSA / RRSP 等の口座で回すことは計画に無い。**既定値へ丸めない**——
    // 「不明なら信用口座」に倒すことが、現金口座で GFV 回避ガードが無効のまま回る事故そのものである。
    public static MoomooAccountType? MapAccountType(int accType) => accType switch
    {
        (int)TrdCommon.TrdAccType.TrdAccType_Cash => MoomooAccountType.Cash,
        (int)TrdCommon.TrdAccType.TrdAccType_Margin => MoomooAccountType.Margin,
        _ => null,
    };

    // #375, ADR-0021 決定3, IADR-0153 決定3: **呼ばれるたびにブローカーへ照会し直す。**
    // 種別が不明なら null（呼び出し側＝アダプタが「照会できなかった」として扱う）。
    //
    // **接続時のスナップショットを返してはならない。** そうすると観測の鮮度（有効期間 30 分）が
    // 「最後にブローカーへ聞いた時刻」ではなく「最後に自分のキャッシュを読んだ時刻」を測ることになる。
    // プロセスが起動しっぱなしで OpenD 接続が切れない限り、接続時に掴んだ種別が「30 分以内に確認済み」の
    // 体裁で更新され続け、**決定3 が意図する「照会結果と設定値の食い違いを都度検知する」効果が働かない**。
    // 現金口座なのに古い信用口座の観測が生き続けると、現金口座統制（GFV 回避・差金決済の米国株拡張）が
    // 発火しないまま新規建てが通り得る。可用性の判定（IsOperationalAsync）が毎回ライブで問い合わせているのと
    // 揃える。呼び出しは可用性 probe の巡回（既定 5 分）のみであり、**発注経路には乗らない**。
    public async Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var (accId, accType) = await FetchSimulateAccountAsync(cancellationToken).ConfigureAwait(false);

        // 照会で得た口座が**発注に用いる口座**（接続時に確定した _simAccId・BuildHeader が使う）と異なる場合、
        // 返す種別は発注先とは別の口座を説明していることになる。種別を偽るより不明（null）へ倒す。
        // _simAccId 自体はここで書き換えない——発注中のヘッダ構築と競合させないためであり、
        // 口座が入れ替わったのなら再接続で確定させるのが筋である。
        if (accId != _simAccId)
        {
            _logger.LogWarning(
                "照会した SIMULATE 口座 accId={FetchedAccId} が発注先 accId={OrderAccId} と異なるため口座種別を不明として扱います。",
                accId,
                _simAccId);
            return null;
        }

        _simAccType = accType;
        return accType;
    }

    // FR-10, #869, ADR-0041 決定2, IADR-0354: 口座の評価額（資産純値・USD）を**呼ばれるたびに照会し直す**
    // （GetAccountTypeAsync と同じ理由——接続時のスナップショットを返すと鮮度が「自分のキャッシュを読んだ時刻」になる）。
    //
    // **通貨を明示して要求し、応答の通貨も確かめる。** moomoo は口座の基準通貨で返し得るため、
    // 要求だけで信じると JPY 建ての数値を USD の統制上限の分母に据えることになる（桁が 2 つずれる）。
    // **買付余力（Power）で代替しない**——信用で 2 倍になり、統制が黙って 2 倍に緩む（ADR-0016 決定6 と同じ論拠）。
    public async Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var c2s = TrdGetFunds.C2S.CreateBuilder()
            .SetHeader(BuildHeader((int)TrdCommon.TrdMarket.TrdMarket_US))
            .SetRefreshCache(true)
            .SetCurrency((int)TrdCommon.Currency.Currency_USD)
            .Build();
        var req = TrdGetFunds.Request.CreateBuilder().SetC2S(c2s).Build();
        var rsp = (TrdGetFunds.Response)await SendAsync(() => _connection.GetFunds(req), cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(rsp.RetType, rsp.RetMsg, "GetFunds");

        var funds = rsp.S2C.Funds;
        if (!funds.HasTotalAssets)
        {
            _logger.LogWarning("口座照会の応答に資産純値（TotalAssets）がありません。基準資金は未供給として扱います。");
            return null;
        }

        if (funds.HasCurrency && funds.Currency != (int)TrdCommon.Currency.Currency_USD)
        {
            _logger.LogWarning(
                "口座照会の応答通貨が USD ではありません currency={Currency}。基準資金は未供給として扱います。",
                funds.Currency);
            return null;
        }

        return (decimal)funds.TotalAssets;
    }

    private TrdCommon.TrdHeader BuildHeader(int trdMarket) =>
        TrdCommon.TrdHeader.CreateBuilder()
            .SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate) // SIMULATE 固定（実弾を撃たない）
            .SetAccID(_simAccId)
            .SetTrdMarket(trdMarket)
            .Build();

    // 対応市場（TrdMarket, SecMarket）。照会/取消でキャッシュミス時に順に試す対象でもある。
    private static readonly (int TrdMarket, int SecMarket)[] SupportedMarkets =
    [
        ((int)TrdCommon.TrdMarket.TrdMarket_US, (int)TrdCommon.TrdSecMarket.TrdSecMarket_US),
        ((int)TrdCommon.TrdMarket.TrdMarket_JP, (int)TrdCommon.TrdSecMarket.TrdSecMarket_JP),
    ];

    // MoomooMarket → (TrdMarket, TrdSecMarket)。SDK 非依存の写像（単体テスト対象）。
    public static (int TrdMarket, int SecMarket) MapMarket(MoomooMarket market) => market switch
    {
        MoomooMarket.Japan => ((int)TrdCommon.TrdMarket.TrdMarket_JP, (int)TrdCommon.TrdSecMarket.TrdSecMarket_JP),
        _ => ((int)TrdCommon.TrdMarket.TrdMarket_US, (int)TrdCommon.TrdSecMarket.TrdSecMarket_US),
    };

    // OpenD OrderStatus（TrdCommon.OrderStatus）を moomoo アダプタの状態へ写像する（SDK 非依存・単体テスト対象）。
    //
    // 🔴 FR-10, UC-06, #848, IADR-0117（2026-09-19 追記・改定 3）: **「確認できた失敗」と「不明」を分ける。**
    // 従来は既定（`_`）が Failed であり、NONE（-1）・TIMEOUT（4。OpenD 定義上「結果未知」）・本実装が知らない
    // 新コードまで Failed → OrderStatus.Rejected（終端）へ畳んでいた。拒否がリスク管理の**在庫解放の引き金**に
    // なった時点で、この既定は fail-safe から fail-open へ反転した（状態が分からないまま建玉の押さえを解く＝
    // 二重決済で意図しないショート化）。**知らないコードは Unknown へ倒す。**
    public static MoomooOrderState MapState(int openDStatus) => openDStatus switch
    {
        0 or 1 or 2 => MoomooOrderState.Submitting,      // Unsubmitted / WaitingSubmit / Submitting
        5 => MoomooOrderState.Submitted,                 // Submitted
        10 => MoomooOrderState.FilledPart,               // Filled_Part
        11 => MoomooOrderState.FilledAll,                // Filled_All
        12 or 13 => MoomooOrderState.Submitted,          // Cancelling_*（取消進行中・まだ有効）
        14 or 15 or 24 => MoomooOrderState.Cancelled,    // Cancelled_Part / Cancelled_All / FillCancelled
        3 or 21 or 22 or 23 => MoomooOrderState.Failed,  // SubmitFailed / Failed / Disabled / Deleted（**確認できた失敗**）
        _ => MoomooOrderState.Unknown,                   // NONE(-1) / TimeOut(4) / 未知の新コード（**結果が分からない**）
    };

    // ---- 応答相関 ----

    private Task<object> SendAsync(Func<uint> send, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        // send() 直後にコールバックが返るレースを防ぐため、serial 採番と登録を _sendGate 内で原子的に行う。
        // Complete も同じロックを取るので、登録前に応答が握りつぶされることはない。
        lock (_sendGate)
        {
            var serial = send();
            _pending[serial] = tcs;
        }
        return tcs.Task.WaitAsync(_replyTimeout, cancellationToken);
    }

    private void Complete(uint serial, object rsp)
    {
        TaskCompletionSource<object>? tcs;
        lock (_sendGate)
        {
            _pending.TryRemove(serial, out tcs);
        }
        if (tcs is not null)
        {
            tcs.TrySetResult(rsp);
        }
    }

    // #821, IADR-0347: 非成功は **retType / retMsg を保つ例外**で投げる（従来のメッセージ文字列は不変。
    // MoomooTradeRequestException は InvalidOperationException 派生であり、既存の捕捉は 1 行も変わらない）。
    // S3（代替注文種別）は「拒否理由を監査台帳へ残すこと」自体が目的であり、文字列へ畳むと取り出せない。
    //
    // 🔴 #848, IADR-0117（2026-09-19 追記・改定 8）: **ここで投げる例外は「拒否」を意味しない。**
    // retType は OpenD の返事（-1＝Failed）だけでなく、SDK がクライアント側で合成する「返事を読めなかった」
    //（-100＝送信済み要求の 12 秒打ち切り／-500＝届いた応答の復号・パース失敗）も運ぶ。本メソッドは分類しない
    //（照会・取消も通るため）。**発注の分類はアダプタが MoomooTradeRequestException.IsConfirmedFailure で行う。**
    private static void EnsureSucceeded(int retType, string retMsg, string op)
    {
        if (retType != 0) // RetType_Succeed=0
        {
            throw new MoomooTradeRequestException(op, retType, retMsg);
        }
    }

    // ---- MMSPI_Conn ----

    public void OnInitConnect(MMAPI_Conn client, long errCode, string desc)
    {
        var tcs = _connectTcs;
        if (errCode == 0)
        {
            _logger.LogInformation("OpenD 接続確立 connID={ConnId}", client.GetConnectID());
            tcs?.TrySetResult(errCode);
        }
        else
        {
            _logger.LogError("OpenD 接続失敗 errCode={ErrCode} desc={Desc}", errCode, desc);
            tcs?.TrySetException(new InvalidOperationException($"OpenD 接続失敗 errCode={errCode}: {desc}"));
        }
    }

    public void OnDisconnect(MMAPI_Conn client, long errCode)
    {
        _connected = false;
        // #732: 切断後も同じ固着に入り得るため、次の接続は作り直してから張る（issue の方針 2）。
        _connectionStale = true;
        _logger.LogWarning("OpenD 切断 errCode={ErrCode}", errCode);
    }

    // ---- MMSPI_Trd（使用するコールバック）----

    public void OnReply_GetAccList(MMAPI_Conn client, uint nSerialNo, TrdGetAccList.Response rsp) => Complete(nSerialNo, rsp);
    public void OnReply_PlaceOrder(MMAPI_Conn client, uint nSerialNo, TrdPlaceOrder.Response rsp) => Complete(nSerialNo, rsp);
    public void OnReply_GetOrderList(MMAPI_Conn client, uint nSerialNo, TrdGetOrderList.Response rsp) => Complete(nSerialNo, rsp);
    public void OnReply_ModifyOrder(MMAPI_Conn client, uint nSerialNo, TrdModifyOrder.Response rsp) => Complete(nSerialNo, rsp);
    // #141, IADR-0092: リコンサイル照会（滞留 Reserved の remark 突合）で履歴注文を列挙する。
    public void OnReply_GetHistoryOrderList(MMAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderList.Response rsp) => Complete(nSerialNo, rsp);
    // #292, IADR-0118: 建玉突合の照会。応答を捨てると GetPositionsAsync が応答待ちのままタイムアウトする。
    public void OnReply_GetPositionList(MMAPI_Conn client, uint nSerialNo, TrdGetPositionList.Response rsp) => Complete(nSerialNo, rsp);
    // FR-10, #869, ADR-0041 決定2, IADR-0354: 基準資金（口座の評価額）の照会。応答を捨てると
    // GetAccountEquityInBaseAsync が応答待ちのままタイムアウトする（建玉照会と同型の事故）。
    public void OnReply_GetFunds(MMAPI_Conn client, uint nSerialNo, TrdGetFunds.Response rsp) => Complete(nSerialNo, rsp);

    // ---- MMSPI_Trd（未使用・no-op）----

    public void OnReply_UnlockTrade(MMAPI_Conn client, uint nSerialNo, TrdUnlockTrade.Response rsp) { }
    public void OnReply_SubAccPush(MMAPI_Conn client, uint nSerialNo, TrdSubAccPush.Response rsp) { }
    public void OnReply_GetMaxTrdQtys(MMAPI_Conn client, uint nSerialNo, TrdGetMaxTrdQtys.Response rsp) { }
    public void OnReply_GetComboMaxTrdQtys(MMAPI_Conn client, uint nSerialNo, TrdGetComboMaxTrdQtys.Response rsp) { }
    public void OnReply_GetOrderFillList(MMAPI_Conn client, uint nSerialNo, TrdGetOrderFillList.Response rsp) { }
    public void OnReply_GetHistoryOrderFillList(MMAPI_Conn client, uint nSerialNo, TrdGetHistoryOrderFillList.Response rsp) { }
    public void OnReply_GetMarginRatio(MMAPI_Conn client, uint nSerialNo, TrdGetMarginRatio.Response rsp) { }
    public void OnReply_GetOrderFee(MMAPI_Conn client, uint nSerialNo, TrdGetOrderFee.Response rsp) { }
    public void OnReply_GetFlowSummary(MMAPI_Conn client, uint nSerialNo, TrdFlowSummary.Response rsp) { }
    public void OnReply_PlaceComboOrder(MMAPI_Conn client, uint nSerialNo, TrdPlaceComboOrder.Response rsp) { }
    public void OnReply_UpdateOrder(MMAPI_Conn client, uint nSerialNo, TrdUpdateOrder.Response rsp) { }
    public void OnReply_UpdateOrderFill(MMAPI_Conn client, uint nSerialNo, TrdUpdateOrderFill.Response rsp) { }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _connection.Close();
            _connection.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenD クライアントの解放中に例外");
        }
        _connectGate.Dispose();
    }
}
