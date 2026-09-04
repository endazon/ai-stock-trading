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

    private readonly Meter _meter;
    private readonly Counter<long> _informationItemsCollected;
    private readonly Counter<long> _tradeCycleDecisions;
    private readonly Histogram<double> _tradeCycleDecisionDurationMs;
    private readonly Histogram<double> _tradeCycleOrderCompletionLatencyMs;
    private readonly Histogram<double> _tradeCycleRecordCompletionLatencyMs;
    private readonly Counter<long> _tradeCycleLatencyUnobserved;
    private readonly Counter<long> _riskScreenings;
    private readonly Counter<long> _riskRejections;
    private readonly Counter<long> _orderExecutions;
    private readonly Counter<long> _orderDispatchForgone;
    private readonly Counter<double> _llmCostJpy;
    private readonly Gauge<double> _llmCostLimitRatioPercent;
    private readonly Gauge<long> _finnhubDailyVolumeEstimate;
    private readonly Gauge<double> _finnhubDailyVolumeLimitRatioPercent;

    /// <param name="meterName">
    /// Meter 名。既定は <see cref="BusinessMetricNames.MeterName"/> であり、**本番では必ず既定を使う**。
    /// <para>
    /// 🔴 <b>テストが「計器が発火しなかった」ことを表明するためだけの引数である</b>（#695）。
    /// <see cref="Meter"/> はプロセス全体で観測されるため、既定名のままだと
    /// <c>MeterCapture</c> が<b>同時に走っている別テストの測定値まで拾い</b>、否定形の表明
    /// （<c>ValuesOf(...).Should().BeEmpty()</c>）が**他人の発火で偽陽性になる**——2026-09-04 の
    /// Integration E2E で実際に起きた（`ast.finnhub.daily_request_estimate` に値 24.0 が混入）。
    /// テストごとに一意な名前を与えれば、捕捉の母集合がそのテストの発火だけに閉じる。
    /// </para>
    /// </param>
    public BusinessMetrics(string? meterName = null)
    {
        _meter = new Meter(meterName ?? BusinessMetricNames.MeterName);

        // 🔴 unit は与えない。単位は名前へ埋めてある（BusinessMetricNames の説明を参照）。
        _informationItemsCollected = _meter.CreateCounter<long>(
            BusinessMetricNames.InformationItemsCollected,
            description: "収集・正規化・KB 保存まで完了した情報アイテム数（FR-01/FR-02）");

        _tradeCycleDecisions = _meter.CreateCounter<long>(
            BusinessMetricNames.TradeCycleDecisions,
            description: "取引判断の回数（action=buy/sell/no-trade・trigger 別。FR-04）");

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
            description: "Finnhub 日次要求見積りが暫定上限に占める割合（%）。100 超で警告（ADR-0031 決定3）");
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
    /// FR-01, ADR-0031（計画）決定2〜3, IADR-0292: プロセスの Finnhub 日次要求見積りと、暫定上限に対する比率を記録する。
    /// </summary>
    public void RecordFinnhubDailyVolumeEstimate(long estimatedDailyRequests, double limitRatioPercent)
    {
        _finnhubDailyVolumeEstimate.Record(estimatedDailyRequests);
        _finnhubDailyVolumeLimitRatioPercent.Record(limitRatioPercent);
    }

    public void Dispose() => _meter.Dispose();
}
