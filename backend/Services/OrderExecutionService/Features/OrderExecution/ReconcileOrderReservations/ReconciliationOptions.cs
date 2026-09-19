namespace OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;

// #141, FR-05, IADR-0074: 発注予約の自動リコンサイル（Reserved 滞留の解消）の構成。
//
// **アプリの既定は 3 つとも無効**。リポの fail-safe 既定（Broker=paper・外部連携空=no-op・retention も無効）に
// 合わせた明示オプトインであり、docker-compose・単体開発環境の挙動を変えないためにここは変えない。
// 有効化しても既定の no-op プローブ（IndeterminateReservationBrokerProbe）下では
// Placed/NotPlaced 経路は発火せず、phase-4 自己修復のみ（ブローカ非依存）が作動する。
//
// 🔴 #856, IADR-0362: **配備（deploy/helm/ai-stock-trading/values.yaml）では Enabled / UseBrokerProbe を有効にし、
// ReleaseOnNotPlaced だけを閉じたままにしている。** 「有効化」と「解放の解禁」は別のスイッチである
// ——滞留の解消（Placed 側）は先に成立させられるが、解放（NotPlaced 側）の誤判定は二重発注に直結する。
public sealed class ReconciliationOptions
{
    public const string SectionName = "Reconciliation";

    /// <summary>リコンサイルを有効にするか（fail-safe 既定: 無効＝走査しない。配備では true）。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// #141, IADR-0092: 実照会プローブ（moomoo/OpenD SIMULATE）を使うか（fail-safe 既定: false＝no-op プローブ）。
    /// true かつ Broker:Provider=moomoo のときだけ実プローブが配線される。paper／OpenD 無しでは無視され、
    /// 既定の no-op プローブ（常に Indeterminate＝何も解放・終端化しない）のままになる。
    /// </summary>
    public bool UseBrokerProbe { get; set; }

    /// <summary>
    /// 🔴 #856, FR-05, IADR-0362: 照会が <c>NotPlaced</c>（未発注）と答えたときに**予約を解放してよいか**
    /// （fail-safe 既定: false＝解放しない）。
    ///
    /// **解放は「再発注を許可する」操作であり、誤判定は二重発注に直結する。** <c>NotPlaced</c> の根拠は
    /// 「発注時に伝播した remark（client order id）で全市場・現在＋履歴を成功裏に列挙して一致ゼロ」であり、
    /// 構造としては筋が通っているが、**moomoo SIMULATE が remark を往復させるかは実機未検証**である
    /// （往復しなければ発注済みの注文が 1 件も一致せず、全件が <c>NotPlaced</c>＝全件解放になる）。
    /// この門を開けてよいのは、実機で <c>NotPlaced</c> の偽陽性が無いことを記録つきで示した後だけである（#856）。
    ///
    /// 🔴 本フラグは <c>NotPlaced</c> にしか効かない。<c>Indeterminate</c> は開けても据え置く（T-10-602）。
    /// </summary>
    public bool ReleaseOnNotPlaced { get; set; }

    /// <summary>
    /// 滞留とみなす閾値（時間）。既定 24 時間（配備は 2 時間。#856 / IADR-0362 —— 24 時間は保護レグの据え置き
    /// ＝無保護の建玉が残っている状態を解くには遅すぎる）。
    /// ⚠️ #856 監査 N6: **23 時間を超える値にしない。** 約定追跡の追跡上限
    /// （<see cref="PollOrderFills.FillPollingOptions.MaxTrackingHours"/>・既定 24 時間）を超えると、
    /// 突合が Placed で記録を作った時点で既に追跡窓の外にあり、非終端のまま取り残される。<see cref="ReconciliationPolicy.MinimumStallThresholdHours"/>
    /// 未満を設定しても下限クランプされるため、in-flight の予約に触れることはない。
    /// </summary>
    public int StallThresholdHours { get; set; } = ReconciliationPolicy.DefaultStallThresholdHours;

    /// <summary>巡回間隔（時間）。既定 6 時間（配備は下限の 1 時間。#856 / IADR-0362）。</summary>
    public int IntervalHours { get; set; } = 6;

    /// <summary>1 巡回で処理する最大件数。</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// 巡回間隔。下限 1 時間（設定ミスで高頻度に回らないため）／上限 1 年（<see cref="TimeSpan.FromHours(double)"/> の
    /// オーバーフローで BackgroundService 起動時に落ちるのを防ぐため）。
    /// </summary>
    public TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(IntervalHours, 1, MaximumIntervalHours));

    /// <summary>巡回間隔の上限（時間）。1 年。これを超える間隔は「実質無効」であり、無効化は Enabled で行う。</summary>
    public const int MaximumIntervalHours = 24 * 365;

    /// <summary>1 巡回の処理上限（下限 1・上限 10000）。</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 10_000);
}
