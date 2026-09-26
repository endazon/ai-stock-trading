using System.Diagnostics.Metrics;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Observability;

/// <summary>
/// NFR-07, NFR-13, #287, IADR-0255: <b>業務メトリクスの計器。</b>DI シングルトンとして各サービスへ供給する。
/// <para>
/// 既存の可観測性は技術指標（ASP.NET Core / HttpClient / .NET ランタイム）だけであり、
/// 「事後に追える」（ログ・トレース）が「異常に気づける」（メトリクス）になっていなかった。
/// 本クラスは<b>統制と取引サイクルの健全性</b>を見るための最小集合を計上する。
/// </para>
/// <para>
/// <b>計装は常に有効だが、外部へ送るかどうかは本クラスの外で決まる</b>（IADR-0094 の opt-in の作法）。
/// 計器は in-process の <see cref="Meter"/> へ記録するだけであり、OTLP でどこへ出るかは
/// otel-collector の exporter 構成が決める（dev 既定は <c>debug</c>＝標準出力のみ・外部送信なし）。
/// </para>
/// <para>
/// <b>タグの基数を増やさない。</b>銘柄・注文 ID・DecisionId はタグにしない——1 系列が銘柄数だけ増えると
/// Prometheus のカーディナリティが業務量に比例して膨らむ。銘柄単位の追跡はログ（Loki）とトレース（Tempo）が担う。
/// </para>
/// </summary>
public sealed class BusinessMetrics : IDisposable
{
    /// <summary>判断が発注意図を作らなかった（方針なし・Hold・見送り）ことを表す <c>action</c> タグ値。</summary>
    public const string ActionNoTrade = "no-trade";

    /// <summary>定時サイクル（情報収集の完了）起点であることを表す <c>trigger</c> タグ値。</summary>
    public const string TriggerScheduled = "scheduled";

    /// <summary>価格変動検知起点であることを表す <c>trigger</c> タグ値。</summary>
    public const string TriggerPriceMovement = "price-movement";

    /// <summary>審査が承認したことを表す <c>outcome</c> タグ値。</summary>
    public const string OutcomeApproved = "approved";

    /// <summary>審査が拒否したことを表す <c>outcome</c> タグ値。</summary>
    public const string OutcomeRejected = "rejected";

    /// <summary>NFR-01, #689: 端点間計測の区間タグ値。起点イベント → 発注完了（<c>OrderExecuted</c> の発行）。</summary>
    public const string StageOrderCompletion = "order-completion";

    /// <summary>NFR-02, #689: 端点間計測の区間タグ値。起点イベント → 記録完了（監査台帳への記録）。</summary>
    public const string StageRecordCompletion = "record-completion";

    /// <summary>
    /// NFR-01, NFR-02, #689: 未観測の理由タグ値。**起点（trigger / 起点時刻）を持たない**注文である。
    /// 取引サイクル外の経路（owner 手仕舞い・維持証拠金の自動縮小・約定追跡の後追い）で正常に発生する。
    /// </summary>
    public const string UnobservedOriginMissing = "origin-missing";

    /// <summary>
    /// NFR-01, NFR-02, #689: 未観測の理由タグ値。終点が起点より**前**になった（サービス間の時計ずれ）。
    /// 負の所要をヒストグラムへ入れると分位点が下振れするため、値を捨てて件数だけ残す。
    /// </summary>
    public const string UnobservedNegativeElapsed = "negative-elapsed";

    /// <summary>
    /// FR-10, #942, IADR-0395: 追随を打ち切った理由タグ値。建玉照会が**不明（<c>null</c>）**を返した。
    /// 🔴 空の一覧（照会は成功・0 株）はこれに当たらない —— それは「確かめた」であり、追随は進む。
    /// </summary>
    public const string DriftFollowUpPositionsUnknown = "positions-unknown";

    /// <summary>FR-10, #942, IADR-0395: 追随を打ち切った理由タグ値。建玉照会が**例外**で落ちた。</summary>
    public const string DriftFollowUpPositionsQueryFailed = "positions-query-failed";

    /// <summary>
    /// FR-03, FR-10, #957, IADR-0399: 保有の行を評価に渡せなかった（識別項目が無い・列挙が未定義・数量が正でない・null の行）。
    /// </summary>
    public const string PositionRowIdentityMissing = "identity-missing";

    /// <summary>FR-03, FR-10, #957, IADR-0399: 損切りラインが無い／正でない行を、平均取得単価からの近似のラインで評価した。</summary>
    public const string PositionRowStopLineApproximated = "stop-line-approximated";

    /// <summary>FR-03, FR-10, #957, IADR-0399: 損切りラインも平均取得単価も無く、評価に渡せなかった。</summary>
    public const string PositionRowStopLineUnknown = "stop-line-unknown";

    /// <summary>FR-03, FR-10, #957, IADR-0399: 200 の応答の本文が保有の一覧として読めなかった（壊れた JSON・<c>null</c> 等）。</summary>
    public const string PositionRowsResponseUnreadable = "response-unreadable";

    private readonly Meter _meter;
    private readonly Counter<long> _informationItemsCollected;
    private readonly Counter<long> _tradeCycleDecisions;
    private readonly Counter<long> _tradeCycleDecisionSkips;
    private readonly Histogram<double> _tradeCycleDecisionDurationMs;
    private readonly Histogram<double> _tradeCycleOrderCompletionLatencyMs;
    private readonly Histogram<double> _tradeCycleRecordCompletionLatencyMs;
    private readonly Counter<long> _tradeCycleLatencyUnobserved;
    private readonly Counter<long> _riskScreenings;
    private readonly Counter<long> _riskRejections;
    private readonly Counter<long> _orderExecutions;
    private readonly Counter<long> _orderDispatchForgone;
    private readonly Counter<long> _driftAdoptionFollowUpAbandoned;
    private readonly Counter<double> _llmCostJpy;
    private readonly Gauge<double> _llmCostLimitRatioPercent;
    private readonly Gauge<long> _finnhubDailyVolumeEstimate;
    private readonly Gauge<double> _finnhubDailyVolumeLimitRatioPercent;
    private readonly Counter<long> _riskCapitalBaselineReads;
    private readonly Counter<long> _marketMonitorPositionRowsDegraded;
    private readonly Counter<long> _finnhubSymbolSetResolutions;
    private readonly Gauge<long> _finnhubSymbolsDeferred;

    /// <summary>
    /// 本番の構築点。Meter 名は <see cref="BusinessMetricNames.MeterName"/> 固定である。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>DI が解決できる公開コンストラクタはこれ 1 本だけに保つ。</b> 引数つきの公開
    /// コンストラクタを足すと <c>AddSingleton&lt;BusinessMetrics&gt;()</c> が <c>string</c> を
    /// 解決できず、**consumer が起動できないまま MassTransit のハーネスが 30 秒でタイムアウトする**
    /// という遠い形で壊れる（#695 の実装中に実測。原因が DI だと分かりにくい）。
    /// テスト用の隔離は <see cref="WithMeterName(string)"/> を使うこと。
    /// </remarks>
    public BusinessMetrics()
        : this(BusinessMetricNames.MeterName)
    {
    }

    /// <summary>
    /// **テスト専用**: Meter 名を差し替えて構築する。
    /// <para>
    /// 🔴 <b>否定形の表明（「計器が発火しなかった」）のためだけにある</b>（#695・IADR-0309）。
    /// <see cref="Meter"/> はプロセス全体で観測されるため、既定名のままだと <c>MeterCapture</c> が
    /// <b>同時に走っている別テストの測定値まで拾い</b>、<c>ValuesOf(...).Should().BeEmpty()</c> が
    /// **他人の発火で偽陽性になる** —— 2026-09-04 の Integration E2E で実際に起きた
    /// （<c>ast.finnhub.daily_request_estimate</c> に値 24.0 が混入して赤）。
    /// </para>
    /// <para>
    /// <b>本番からは呼ばない。</b> 肯定形の表明も既定名のままでよい（他人の測定値が混ざっても
    /// 「含む」の表明は壊れない）。
    /// </para>
    /// </summary>
    public static BusinessMetrics WithMeterName(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        return new BusinessMetrics(meterName);
    }

    private BusinessMetrics(string meterName)
    {
        _meter = new Meter(meterName);

        // 🔴 unit は与えない。単位は名前へ埋めてある（BusinessMetricNames の説明を参照）。
        _informationItemsCollected = _meter.CreateCounter<long>(
            BusinessMetricNames.InformationItemsCollected,
            description: "収集・正規化・KB 保存まで完了した情報アイテム数（FR-01/FR-02）");

        _tradeCycleDecisions = _meter.CreateCounter<long>(
            BusinessMetricNames.TradeCycleDecisions,
            description: "取引判断の回数（action=buy/sell/no-trade・trigger 別。FR-04）");

        // FR-04, FR-10, #891, IADR-0374: 見送りの理由の内訳。**上の decisions を置き換えない**
        // （タグを増やすと既存ダッシュボードの集計が割れる。別カウンタとして並べる）。
        _tradeCycleDecisionSkips = _meter.CreateCounter<long>(
            BusinessMetricNames.TradeCycleDecisionSkips,
            description: "発注意図を作らなかった判断の理由の内訳（reason・trigger 別。FR-04/FR-10）");

        _tradeCycleDecisionDurationMs = _meter.CreateHistogram<double>(
            BusinessMetricNames.TradeCycleDecisionDurationMs,
            description: "取引判断 1 回の所要ミリ秒（FR-04）");

        // NFR-01, NFR-02, #689, IADR-0307: 端点間レイテンシ。**バケット境界は View で明示する**
        // （ObservabilityExtensions）——既定境界は上限 10,000 ms であり、5 分 / 10 分の目標値は
        // すべて +Inf に落ちて分位点が意味を失う。
        _tradeCycleOrderCompletionLatencyMs = _meter.CreateHistogram<double>(
            BusinessMetricNames.TradeCycleOrderCompletionLatencyMs,
            description: "起点イベントから発注完了までのミリ秒（trigger 別。NFR-01）");

        _tradeCycleRecordCompletionLatencyMs = _meter.CreateHistogram<double>(
            BusinessMetricNames.TradeCycleRecordCompletionLatencyMs,
            description: "起点イベントから記録完了までのミリ秒（trigger 別。NFR-02）");

        _tradeCycleLatencyUnobserved = _meter.CreateCounter<long>(
            BusinessMetricNames.TradeCycleLatencyUnobserved,
            description: "端点間の所要を確定できなかった件数（stage・reason 別。NFR-01/NFR-02）");

        _riskScreenings = _meter.CreateCounter<long>(
            BusinessMetricNames.RiskScreenings,
            description: "発注前審査の回数（outcome=approved/rejected。FR-10/FR-19）");

        _riskRejections = _meter.CreateCounter<long>(
            BusinessMetricNames.RiskRejections,
            description: "発注前審査の拒否理由の内訳（FR-10/FR-19）");

        _orderExecutions = _meter.CreateCounter<long>(
            BusinessMetricNames.OrderExecutions,
            description: "発注結果（status・provider 別。FR-05）");

        _orderDispatchForgone = _meter.CreateCounter<long>(
            BusinessMetricNames.OrderDispatchForgone,
            description: "発注せずに見送った件数（reason 別。FR-05/FR-10）");

        // FR-10, #942, IADR-0395: 乖離の取り込みの追随を、建玉照会の不明・失敗のまま再試行を使い切って打ち切った件数。
        _driftAdoptionFollowUpAbandoned = _meter.CreateCounter<long>(
            BusinessMetricNames.DriftAdoptionFollowUpAbandoned,
            description: "乖離の取り込みの追随を建玉照会の不明・失敗で再試行を使い切って打ち切った件数（reason 別。FR-10）");

        _llmCostJpy = _meter.CreateCounter<double>(
            BusinessMetricNames.LlmCostJpy,
            description: "計上した LLM 費用（円。category=Llm は月次上限の対象。NFR-13）");

        _llmCostLimitRatioPercent = _meter.CreateGauge<double>(
            BusinessMetricNames.LlmCostLimitRatioPercent,
            description: "当月 LLM 費用が月次上限に占める割合（%）。80 で間隔延長・100 で停止（NFR-13）");

        _finnhubDailyVolumeEstimate = _meter.CreateGauge<long>(
            BusinessMetricNames.FinnhubDailyVolumeEstimate,
            description: "プロセスごとの Finnhub 日次要求見積り（回/日。ADR-0031 決定2〜3）");

        _finnhubDailyVolumeLimitRatioPercent = _meter.CreateGauge<double>(
            BusinessMetricNames.FinnhubDailyVolumeLimitRatioPercent,
            description: "Finnhub 日次要求見積りが、実測して設定した日次上限に占める割合（%）。100 超で警告。上限が未設定（既定）なら記録しない（ADR-0043 決定1）");

        // FR-10, #889, IADR-0372: 基準資金を読んだ結果の内訳。**門ではなく観測である**
        // （どの帰結でも返す値は従来どおり）。
        _riskCapitalBaselineReads = _meter.CreateCounter<long>(
            BusinessMetricNames.RiskCapitalBaselineReads,
            description: "統制上限の基準資金を読んだ結果の内訳（outcome 別。FR-10）");

        // FR-03, FR-10, #957, IADR-0399: 市場監視が保有照会の応答をそのまま評価できなかった行（平常時 0 件）。
        _marketMonitorPositionRowsDegraded = _meter.CreateCounter<long>(
            BusinessMetricNames.MarketMonitorPositionRowsDegraded,
            description: "市場監視が保有照会の応答をそのまま評価できなかった行の件数（reason 別。FR-03/FR-10）");

        // FR-01, FR-13, #1015, IADR-0435: 情報収集の Finnhub の対象銘柄の出所（watchlist 以外は変更が収集に届いていない印）。
        _finnhubSymbolSetResolutions = _meter.CreateCounter<long>(
            BusinessMetricNames.InformationCollectionFinnhubSymbolSetResolutions,
            description: "情報収集が Finnhub の対象銘柄を決めた出所の内訳（outcome 別。FR-01/FR-13）");

        // FR-01, #1015, IADR-0435: 1 巡回に収まらず後回しにした Finnhub の対象銘柄の数（平常時 0）。
        _finnhubSymbolsDeferred = _meter.CreateGauge<long>(
            BusinessMetricNames.InformationCollectionFinnhubSymbolsDeferred,
            description: "情報収集が 1 巡回に収まらず後回しにした Finnhub の対象銘柄の数（計画 ADR-0043 決定2 (b)。FR-01）");
    }

    /// <summary>FR-01, FR-02: 1 巡回で収集できたアイテム数を計上する。</summary>
    public void RecordInformationCollected(int itemCount) =>
        _informationItemsCollected.Add(itemCount);

    /// <summary>
    /// FR-04: 取引判断 1 回を計上する。<paramref name="side"/> が <c>null</c> なら発注意図なし
    /// （方針なし・Hold・見送り）として <see cref="ActionNoTrade"/> で数える。
    /// </summary>
    public void RecordTradeDecision(string trigger, TradeSide? side) =>
        _tradeCycleDecisions.Add(
            1,
            new KeyValuePair<string, object?>(
                BusinessMetricNames.TagAction,
                side is null ? ActionNoTrade : side.Value.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>(BusinessMetricNames.TagTrigger, trigger));

    /// <summary>
    /// FR-04, FR-10, #891, IADR-0374: <b>見送り 1 回を理由つきで計上する。</b>
    /// <para>
    /// 🔴 <b><see cref="RecordTradeDecision"/> の代わりではない。両方を呼ぶ。</b>
    /// 1 回の見送りで <c>decisions{action=no-trade}</c> と <c>decision_skips{reason=…}</c> が
    /// 1 ずつ増える。合計が一致することで、どちらかの計上漏れを突き合わせで検出できる。
    /// </para>
    /// </summary>
    public void RecordTradeDecisionSkipped(string trigger, DecisionSkipReason reason) =>
        _tradeCycleDecisionSkips.Add(
            1,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, reason.ToString()),
            new KeyValuePair<string, object?>(BusinessMetricNames.TagTrigger, trigger));

    /// <summary>FR-04: 取引判断 1 回の所要を計上する。</summary>
    public void RecordTradeDecisionDuration(string trigger, double elapsedMilliseconds) =>
        _tradeCycleDecisionDurationMs.Record(
            elapsedMilliseconds,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagTrigger, trigger));

    /// <summary>
    /// NFR-01, #689, IADR-0307: <b>起点イベント → 発注完了</b>の端点間所要を計上する（NFR-01 の計器）。
    /// <paramref name="cycleTrigger"/> / <paramref name="cycleStartedAt"/> は注文が運んできた
    /// 取引サイクルの起点であり、いずれかが <c>null</c> なら<b>未観測として数える</b>。
    /// </summary>
    public void RecordOrderCompletionLatency(
        string? cycleTrigger, DateTimeOffset? cycleStartedAt, DateTimeOffset completedAt) =>
        RecordCycleLatency(
            _tradeCycleOrderCompletionLatencyMs, StageOrderCompletion, cycleTrigger, cycleStartedAt, completedAt);

    /// <summary>
    /// NFR-02, #689, IADR-0307: <b>起点イベント → 記録完了</b>の端点間所要を計上する（NFR-02 の計器）。
    /// <paramref name="recordedAt"/> は監査台帳へ記録した時刻である。
    /// </summary>
    public void RecordRecordCompletionLatency(
        string? cycleTrigger, DateTimeOffset? cycleStartedAt, DateTimeOffset recordedAt) =>
        RecordCycleLatency(
            _tradeCycleRecordCompletionLatencyMs, StageRecordCompletion, cycleTrigger, cycleStartedAt, recordedAt);

    /// <summary>
    /// NFR-01, NFR-02, #689, IADR-0307: 端点間計測の<b>唯一の判断点</b>。
    /// <para>
    /// 🔴 <b>「測れなかった」と「0 だった」を区別する。</b>起点が無い・経過が負のときは
    /// ヒストグラムへ 1 件も入れず、未観測カウンタへ理由つきで 1 件だけ数える。
    /// 2 つの計上点（発注執行・監査）で判断が割れないよう、分岐はここ 1 か所に持つ。
    /// </para>
    /// </summary>
    private void RecordCycleLatency(
        Histogram<double> histogram,
        string stage,
        string? cycleTrigger,
        DateTimeOffset? cycleStartedAt,
        DateTimeOffset completedAt)
    {
        if (cycleStartedAt is not { } startedAt || string.IsNullOrEmpty(cycleTrigger))
        {
            RecordLatencyUnobserved(stage, UnobservedOriginMissing);
            return;
        }

        var elapsed = (completedAt - startedAt).TotalMilliseconds;
        if (elapsed < 0)
        {
            RecordLatencyUnobserved(stage, UnobservedNegativeElapsed);
            return;
        }

        histogram.Record(
            elapsed,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagTrigger, cycleTrigger));
    }

    /// <summary>NFR-01, NFR-02, #689: 端点間の所要を確定できなかった 1 件を計上する。</summary>
    private void RecordLatencyUnobserved(string stage, string reason) =>
        _tradeCycleLatencyUnobserved.Add(
            1,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagStage, stage),
            new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, reason));

    /// <summary>
    /// FR-10, FR-19: 発注前審査 1 件を計上する。承認なら <paramref name="rejectionReasons"/> は空である。
    /// <b>承認・拒否のいずれでも審査回数を数える</b>（拒否だけを数えると「違反 0 件」と「審査が動いていない」を
    /// 区別できない）。拒否理由は<b>列挙のすべて</b>を 1 件ずつ数える（1 注文に複数の統制が同時に効き得る）。
    /// </summary>
    public void RecordOrderScreening(bool approved, IReadOnlyList<RejectionReason> rejectionReasons)
    {
        ArgumentNullException.ThrowIfNull(rejectionReasons);

        _riskScreenings.Add(
            1,
            new KeyValuePair<string, object?>(
                BusinessMetricNames.TagOutcome, approved ? OutcomeApproved : OutcomeRejected));

        for (var i = 0; i < rejectionReasons.Count; i++)
        {
            _riskRejections.Add(
                1,
                new KeyValuePair<string, object?>(
                    BusinessMetricNames.TagReason, rejectionReasons[i].ToString()));
        }
    }

    /// <summary>FR-05: 発注結果 1 件を計上する。</summary>
    public void RecordOrderExecuted(OrderStatus status, BrokerProvider provider) =>
        _orderExecutions.Add(
            1,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagStatus, status.ToString()),
            new KeyValuePair<string, object?>(BusinessMetricNames.TagProvider, provider.ToString()));

    /// <summary>FR-05, FR-10: 発注を見送った 1 件を計上する。</summary>
    public void RecordOrderDispatchForgone(OrderDispatchForgoneReason reason) =>
        _orderDispatchForgone.Add(
            1,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, reason.ToString()));

    /// <summary>
    /// FR-10, #942, IADR-0395: 乖離の取り込みの追随を、建玉照会の不明・失敗のまま<b>再試行を使い切って</b>打ち切った 1 件を計上する。
    /// <paramref name="reason"/> は <see cref="DriftFollowUpPositionsUnknown"/> か <see cref="DriftFollowUpPositionsQueryFailed"/>。
    /// </summary>
    /// <exception cref="ArgumentException">上の 2 値以外。語彙の外の値で系列を増やさない（基数の規律）。</exception>
    public void RecordDriftAdoptionFollowUpAbandoned(string reason)
    {
        if (reason is not (DriftFollowUpPositionsUnknown or DriftFollowUpPositionsQueryFailed))
        {
            throw new ArgumentException(
                $"追随を打ち切った理由は {DriftFollowUpPositionsUnknown} / {DriftFollowUpPositionsQueryFailed} のいずれかである（実値: '{reason}'）。",
                nameof(reason));
        }

        _driftAdoptionFollowUpAbandoned.Add(
            1, new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, reason));
    }

    /// <summary>
    /// FR-10, #942, IADR-0395: 上のカウンタを<b>理由ごとに 0 で計上し、系列を先に作る</b>。発注執行が起動完了時に 1 度呼ぶ。
    /// <para>
    /// 🔴 <b>なぜ要るか</b>: この事象の平常時の件数は 0 である。系列が最初の打ち切りで初めて現れると、その時点の値は 1 で、
    /// Prometheus の <c>increase()</c> は 1 点目を増分に数えない（前の点が無い）。<b>プロセスの起動から最初の打ち切りを
    /// アラートが取りこぼす</b>——稀な事象ほど、その「最初」が唯一の 1 回になる。
    /// </para>
    /// <para>
    /// 🔴 <b>OTel の MeterProvider が立った後に呼ぶこと。</b> それより前の計上は誰も聞いておらず、何も残らない。
    /// </para>
    /// </summary>
    public void PrimeDriftAdoptionFollowUpAbandoned()
    {
        _driftAdoptionFollowUpAbandoned.Add(
            0, new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, DriftFollowUpPositionsUnknown));
        _driftAdoptionFollowUpAbandoned.Add(
            0, new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, DriftFollowUpPositionsQueryFailed));
    }

    /// <summary>
    /// NFR-13: LLM 費用の計上と、当月の上限消費率を記録する。
    /// <paramref name="category"/> は上限の対象（<c>Llm</c>）か対象外（<c>LlmUncapped</c>）かを表す文字列。
    /// </summary>
    public void RecordLlmCost(string category, decimal amount, decimal limitRatioPercent)
    {
        _llmCostJpy.Add(
            (double)amount,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagCategory, category));
        _llmCostLimitRatioPercent.Record((double)limitRatioPercent);
    }

    /// <summary>
    /// FR-01, ADR-0031（計画）決定2〜3, IADR-0292: プロセスの Finnhub 日次要求見積りと、日次上限に対する比率を記録する。
    /// ADR-0043（計画）決定 1, #1030, IADR-0437: 日次上限は既定で未設定（未実測）であり、そのときは比率を記録しない
    /// （<paramref name="limitRatioPercent"/> が null）。推測の分母で割った比率を出さない。
    /// </summary>
    public void RecordFinnhubDailyVolumeEstimate(long estimatedDailyRequests, double? limitRatioPercent)
    {
        _finnhubDailyVolumeEstimate.Record(estimatedDailyRequests);
        if (limitRatioPercent is { } ratio)
            _finnhubDailyVolumeLimitRatioPercent.Record(ratio);
    }

    /// <summary>
    /// FR-10, #889, IADR-0372: 基準資金を読んだ 1 回を帰結つきで計上する。
    /// <para>
    /// 🔴 <b>「供給できた」の中を割るためにある。</b> 残高 0 を観測した日は行が書かれず、読み出しは
    /// 前取引日の値を返し続ける ——<b>値が返っている以上、統制は平常どおり動いて見える</b>。
    /// <see cref="CapitalBaselineReadOutcome.SuppliedWithGap"/> はその状態を数える唯一の手段である。
    /// </para>
    /// </summary>
    public void RecordCapitalBaselineRead(CapitalBaselineReadOutcome outcome) =>
        _riskCapitalBaselineReads.Add(
            1,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagOutcome, outcome.ToString()));

    /// <summary>
    /// FR-03, FR-10, #957, IADR-0399: 市場監視が保有照会の応答の行（または応答全体）をそのまま評価できなかった 1 件を計上する。
    /// <paramref name="count"/> は同じ理由の件数（1 巡回ぶんをまとめて足す）。0 以下は計上しない。
    /// </summary>
    public void RecordMarketMonitorPositionRowsDegraded(string reason, int count = 1)
    {
        if (count <= 0)
            return;

        _marketMonitorPositionRowsDegraded.Add(
            count,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagReason, reason));
    }

    /// <summary>
    /// FR-01, FR-13, #1015, IADR-0435: 情報収集が Finnhub の対象銘柄を決めた出所を 1 件計上する
    /// （<c>watchlist</c> / <c>last-known</c> / <c>configured-fallback</c>）。
    /// </summary>
    public void RecordFinnhubSymbolSetResolution(string outcome) =>
        _finnhubSymbolSetResolutions.Add(
            1,
            new KeyValuePair<string, object?>(BusinessMetricNames.TagOutcome, outcome));

    /// <summary>
    /// FR-01, #1015, IADR-0435: 直近の決定で後回しにした Finnhub の対象銘柄の数を記録する（0 も記録する＝回復が見える）。
    /// </summary>
    public void RecordFinnhubSymbolsDeferred(long deferred) => _finnhubSymbolsDeferred.Record(deferred);

    public void Dispose() => _meter.Dispose();
}
