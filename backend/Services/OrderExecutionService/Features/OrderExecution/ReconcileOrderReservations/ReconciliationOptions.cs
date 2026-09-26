namespace OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;

// #141, FR-05, IADR-0074: 発注予約の自動リコンサイル（Reserved 滞留の解消）の構成。
//
// **アプリの既定は 3 つとも無効**。リポの fail-safe 既定（Broker=paper・外部連携空=no-op・retention も無効）に
// 合わせた明示オプトインであり、docker-compose・単体開発環境の挙動を変えないためにここは変えない。
// 有効化しても既定の no-op プローブ（IndeterminateReservationBrokerProbe）下では
// Placed/NotPlaced 経路は発火せず、phase-4 自己修復のみ（ブローカ非依存）が作動する。
//
// 🔴 #856, IADR-0362: **配備（deploy/helm/ai-stock-trading/values.yaml）では Enabled / UseBrokerProbe を有効にし、
// ReleaseOnNotPlaced（#1051, IADR-0444: 取引環境ごとの Simulate / Real）だけを閉じたままにしている。** 「有効化」と「解放の解禁」は別のスイッチである
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
    /// （fail-safe 既定: どちらも false＝解放しない）。
    ///
    /// **解放は「再発注を許可する」操作であり、誤判定は二重発注に直結する。** <c>NotPlaced</c> の根拠は
    /// 「発注時に伝播した remark（client order id）で全市場・現在＋履歴を成功裏に列挙して一致ゼロ」であり、
    /// 構造としては筋が通っているが、**remark が往復するかは実機未検証**である
    /// （往復しなければ発注済みの注文が 1 件も一致せず、全件が <c>NotPlaced</c>＝全件解放になる）。
    ///
    /// 🔴 NFR-09, ADR-0045 決定1・決定2, #1051, IADR-0444 決定2: **門は取引環境（SIMULATE / 実弾）ごとに分かれている。**
    /// 構成キーは <c>Reconciliation:ReleaseOnNotPlaced:Simulate</c> / <c>:Real</c>（env: <c>Reconciliation__ReleaseOnNotPlaced__Simulate</c> /
    /// <c>__Real</c>）。どちらの門も、その取引環境の実機の記録で ADR-0045 決定1 の (a)(b) を示した後にだけ開けてよい。
    /// **SIMULATE の記録で実弾の門を開けない。** どの門を使うかは予約ごとに、予約の取引環境で決まる（<see cref="ReleaseGatePolicy"/>）。
    ///
    /// 🔴 門は <c>NotPlaced</c> にしか効かない。<c>Indeterminate</c> は開けても据え置く（T-10-602 / T-10-1606）。
    /// </summary>
    public ReleaseOnNotPlacedGates ReleaseOnNotPlaced { get; set; } = new();

    /// <summary>
    /// #1051, IADR-0444 決定4: 旧キー <c>Reconciliation:ReleaseOnNotPlaced</c>（スカラーの真偽値）を SIMULATE の門へ写したとき、
    /// その元の値（<c>true</c> / <c>false</c>）。写していなければ null（旧キーが無い、または新キーが勝った）。
    /// 起動時の Warning に使う。**構成から束縛されない**（setter が非公開）。
    /// </summary>
    public bool? LegacyReleaseOnNotPlacedMapped { get; private set; }

    /// <summary>
    /// #1051, IADR-0444 決定4: 旧キーが在ったが、新キー <c>…:Simulate</c> も在ったため無視したか。起動時の Warning に使う。
    /// </summary>
    public bool LegacyReleaseOnNotPlacedIgnored { get; private set; }

    /// <summary>
    /// 🔴 #1051, IADR-0444 決定4: 旧キー <c>Reconciliation:ReleaseOnNotPlaced</c>（取引環境を区別しない真偽値）の扱い。
    /// <list type="bullet">
    ///   <item>**SIMULATE の門にだけ写す。実弾の門には決して写さない**（ADR-0045 決定2。旧キーの従前の実効範囲は、実弾が
    ///   <c>LiveTradingGate</c> で到達不能だったため SIMULATE だけであり、写像はその実効範囲を保つ）。</item>
    ///   <item>新キー <c>…:Simulate</c> が在れば新キーが勝つ（旧キーは無視する）。</item>
    ///   <item><c>true</c> / <c>false</c>（大小文字・前後空白は問わない）以外は起動時に止める（従前の束縛も真偽値でなければ止まった）。</item>
    /// </list>
    /// 起動時停止にしないのは、発注執行が再起動を繰り返すと発注と保護逆指値ガードがまとめて止まるためである
    /// （稼働中の PoC は旧キーを <c>"false"</c> で持つ。写した結果は既定と同じ閉であり、何も変わらない）。
    /// </summary>
    /// <param name="legacyValue">
    /// 旧キーの生の値（無ければ null）。🔴 空・空白は「無い」と同じに扱う——子（<c>…:Simulate</c> / <c>…:Real</c>）を持つ節は、
    /// 構成の供給元によって自分自身の値として空文字を返す（本番の組み立てで実測。T-10-1609）。
    /// </param>
    /// <param name="simulateKeyPresent">新キー <c>…:Simulate</c> が構成に在るか。</param>
    public void ApplyLegacyReleaseOnNotPlaced(string? legacyValue, bool simulateKeyPresent)
    {
        if (string.IsNullOrWhiteSpace(legacyValue))
            return;

        if (!bool.TryParse(legacyValue.Trim(), out var legacy))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ReleaseOnNotPlaced '{legacyValue}' は真偽値ではありません。旧キーは廃止しました。"
                + $"取引環境ごとの {SectionName}:ReleaseOnNotPlaced:Simulate / :Real（env: {SectionName}__ReleaseOnNotPlaced__Simulate /"
                + " __Real）を使ってください（#1051 / IADR-0444。どちらも既定は false）。");
        }

        if (simulateKeyPresent)
        {
            LegacyReleaseOnNotPlacedIgnored = true;
            return;
        }

        ReleaseOnNotPlaced.Simulate = legacy;
        LegacyReleaseOnNotPlacedMapped = legacy;
    }

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

// 🔴 NFR-09, ADR-0045 決定2, #1051, IADR-0444 決定2: 取引環境ごとの解放の門。**既定はどちらも閉**。
// 構成キーは Reconciliation:ReleaseOnNotPlaced:Simulate / :Real。
public sealed class ReleaseOnNotPlacedGates
{
    /// <summary>moomoo SIMULATE へ送った予約の門（fail-safe 既定: false）。</summary>
    public bool Simulate { get; set; }

    /// <summary>
    /// moomoo REAL（実弾）へ送った予約の門（fail-safe 既定: false）。🔴 **実弾の記録で ADR-0045 決定1 を満たすまで開けない**
    /// （SIMULATE の記録では開けない。旧キーもここへは写らない）。
    /// </summary>
    public bool Real { get; set; }
}
