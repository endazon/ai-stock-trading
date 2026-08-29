using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using InformationCollectionService.Features.InformationCollection;
using InformationCollectionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Wolverine;
using AppSvc = InformationCollectionService.Features.InformationCollection.InformationCollectionAppService;

namespace InformationCollectionService.Hosted;

// FR-01, FR-02, UC-01: 収集間隔ごとのポーリング。1 巡回で収集→正規化→サニタイズ→KB 保存し、収集があれば InformationCollected
// を発行して取引サイクル（FR-02）の起点にする。巡回の例外は握りつぶしてログする（フェイルセーフ・収集を止めない）。
// NFR（費用）, IADR-0031: 各巡回で費用統制（#23）を照会し、Halted なら巡回をスキップ（サイクル停止）、Throttled なら間隔を延長する。
// NFR-07, #287, IADR-0255: 取引サイクルの起点が動いているかを見るため、1 巡回の収集件数を計上する。
public sealed class CollectionPollingService(
    IServiceScopeFactory scopeFactory,
    IOptions<CollectionOptions> options,
    BusinessMetrics metrics,
    ILogger<CollectionPollingService> logger) : BackgroundService
{
    // Halted 時の再照会倍率（収集はしないが、復帰検知のため Throttled と同じ間隔で回す）。
    private const decimal HaltedRecheckMultiplier = 2m;

    // #564, IADR-0267: 現況観測に載せる有効期間。**実効巡回間隔の 2 倍**（下限 5 分）とする——
    // 1 巡回ぶんの取りこぼし（発行失敗・再配送遅延）で統制が誤発動しない最小の余裕である。
    // 受け手はこの値を過ぎた観測を「無い」と同じに扱い、新規建てを止める（フェイルクローズ）。
    private const double ObservationValidityFactor = 2d;
    private static readonly TimeSpan MinObservationValidity = TimeSpan.FromMinutes(5);

    // #336, ADR-0020 決定2-3: 欠測の遷移判定（発生時刻・継続時間・該当サイクル数）。巡回をまたいで状態を持つため
    // ポーラ（singleton）が保持する。判定そのものは DegradationStateTracker（外部依存なし）が行う。
    // #564: あわせて毎巡回の現況観測を作る（有効期間を与える。受け手は巡回間隔を知らないため発行側が宣言する）。
    private readonly DegradationStateTracker _degradationTracker =
        new(ObservationValidity(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // #121: External（本番スケジューラ=K8s CronJob）では in-process 巡回を行わない。
        // サイクルの起動は run-once エンドポイント（RunOnceAsync）経由。休場ガードは下流 TradeDecision（IADR-0023）。
        if (options.Value.Trigger == CollectionTrigger.External)
        {
            logger.LogInformation(
                "収集トリガは External（スケジューラ駆動）です。in-process ポーリングは停止し、run-once で起動します。");
            return;
        }

        var baseInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var gate = CostControlGate.Normal;
            try
            {
                gate = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "情報収集の巡回でエラーが発生しました。次回巡回を継続します。");
            }

            try
            {
                await Task.Delay(EffectiveInterval(baseInterval, gate), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // #564: 巡回間隔から現況観測の有効期間を導く純関数（境界テスト用）。
    // 🔴 **巡回間隔そのものを渡さない。** 等値にすると、わずかな揺らぎ（発行の遅れ・再配送）で観測が
    // 失効し、健全なのに新規建てが止まる。逆に長すぎれば古い現況を信じ続けるため、受け手側が上限をクランプする。
    public static TimeSpan ObservationValidity(TimeSpan pollInterval)
    {
        var scaled = pollInterval * ObservationValidityFactor;
        return scaled < MinObservationValidity ? MinObservationValidity : scaled;
    }

    // 費用統制の状態から次回巡回までの実効間隔を算出する純関数（境界テスト用）。
    // Normal=base、Throttled=base×（サーバ応答の IntervalMultiplier をそのまま使用・下限1・現状 CostGovernor は 2×）、
    // Halted=base×2（収集はしないが復帰検知のため再照会）。
    public static TimeSpan EffectiveInterval(TimeSpan baseInterval, CostControlGate gate)
    {
        var multiplier = gate.Halted ? HaltedRecheckMultiplier : Math.Max(1m, gate.IntervalMultiplier);
        return baseInterval * (double)multiplier;
    }

    // 1 巡回。費用統制を照会し、Halted なら収集/発行をスキップする。適用する統制ゲートを返す（間隔算出に用いる）。
    // 単体テスト可能な単位として公開する。
    public async Task<CostControlGate> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var gate = await scope.ServiceProvider.GetRequiredService<ICostControlGate>()
            .GetAsync(cancellationToken).ConfigureAwait(false);

        if (gate.Halted)
        {
            // NFR（費用）: LLM 月次上限 100% 到達＝停止。収集も発行もしない（サイクルを回さない）。
            logger.LogWarning("費用統制が停止（Halted）のため今回の収集巡回をスキップします。");
            return gate;
        }

        var collector = scope.ServiceProvider.GetRequiredService<AppSvc>();
        // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。巡回ごとのスコープから解決する
        // （Wolverine の PublishAsync は CancellationToken を取らない。巡回の中断は上位のループが見る）。
        var publish = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var result = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);

        // FR-01, FR-09, FR-11, #336, ADR-0020 決定2-3: 欠測の遷移を記録・通知する（継続中は黙る）。
        // 発行はクリティカルパス外であり、失敗しても収集を壊さない（状態は巻き戻して次回に再発行する）。
        await PublishDegradationSafeAsync(publish, result.Degradation).ConfigureAwait(false);

        // 🔴 ADR-0020 決定3: **サイクル中止**（moomoo 等の必須ソースの欠測）は取引サイクルを起こさない。
        // ここで止めるのは**新規の判断サイクル**だけであり、**手仕舞い・損切りは別経路**である
        // （損切りの実行機構はブローカー側の逆指値であり、系が止まっても効く・NFR-04）。
        if (result.Degradation.AbortCycle)
        {
            logger.LogError(
                "必須情報源の欠測により当該サイクルを中止します（フェイルセーフ）。欠測: {Missing}。"
                + "手仕舞い・損切りは止めていません。",
                string.Join(", ", result.Degradation.MissingRequired));
            return gate;
        }

        // NFR-07, #287: 収集件数は**空巡回（0 件）でも計上する**——0 を計上しないと「巡回が回って 0 件だった」と
        // 「巡回そのものが止まっている」を区別できない（カウンタが伸びないという同じ形になる）。
        metrics.RecordInformationCollected(result.ItemCount);

        // 収集があった場合のみ取引サイクルの起点イベントを発行する（空巡回では起動しない）。
        if (result.ItemCount > 0)
        {
            await publish.PublishAsync(
                new InformationCollected(Guid.NewGuid(), result.ItemCount, DateTimeOffset.UtcNow))
                .ConfigureAwait(false);
        }

        return gate;
    }

    // #336, ADR-0020 決定2-3: 欠測・回復の遷移イベントと、#564 の現況観測を発行する。
    // 🔴 **現況観測は毎巡回 1 件出る。** 受け手（リスク管理）はこれが途切れると新規建てを止めるため、
    // External トリガ（K8s CronJob）で運用するときは **Collection:PollIntervalSeconds を cron の周期と揃える**こと
    // （揃わない場合は観測が早く失効し、新規建てが止まる＝安全側に落ちる）。
    // fail-safe: 発行の失敗で収集を止めない。**ただし状態は巻き戻す**——巻き戻さないと「発行済み」として
    // 記録され、次の機会にも二度と出なくなる（IADR-0196 と同じ理由）。
    private async Task PublishDegradationSafeAsync(IMessageBus publish, CollectionDegradation degradation)
    {
        foreach (var message in _degradationTracker.Observe(degradation, DateTimeOffset.UtcNow))
        {
            try
            {
                await publish.PublishAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _degradationTracker.Rollback(message);
                logger.LogError(ex, "情報源の欠測状態の発行に失敗しました（収集は継続します）。");
            }
        }
    }
}
