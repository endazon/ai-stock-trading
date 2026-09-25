using OrderExecutionService.Common.Abstractions;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using OrderExecutionService.Features.OrderExecution.AmendOrder;
using OrderExecutionService.Features.OrderExecution.DispatchApprovedOrder;
using OrderExecutionService.Features.OrderExecution.ExecuteSoftwareStops;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerAvailability;
using OrderExecutionService.Features.OrderExecution.ObserveBrokerPositions;
using OrderExecutionService.Features.OrderExecution.PollOrderFills;
using OrderExecutionService.Features.OrderExecution.ReconcileOrderReservations;
using OrderExecutionService.Features.OrderExecution.RecordTradeExpenses;
using OrderExecutionService.Hosted;
using OrderExecutionService.Infrastructure.ExternalServices;
using OrderExecutionService.Infrastructure.Steps;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Operations;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Wolverine;

const string ServiceName = "ai-stock-trading.order-execution-service";

// #13 Slice A, IADR-0013/0016: ヘルスチェックの HTTP サーフェスのため WebApplication を用いる。
// OrderApproved 購読は Wolverine のハンドラとして稼働する。
//
// IADR-0013: 本 Program.cs の standalone 配線（Wolverine/RabbitMQ・PostgreSQL を shim 経由で組む部分）は
// dev/test/CI でのローカル単体実行のためのもの。本番は platform 統合（#22）で共通基盤に置き換わる。
// IADR-0016: ブローカ既定はペーパー（実弾を撃たない）。moomoo は PoC まで構成ゲートで停止する。
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, logConfig) =>
    logConfig.ConfigureAiStockTradingSerilog(builder.Configuration, ServiceName));
builder.Services.AddAiStockTradingObservability(builder.Configuration, ServiceName);

// ADR-0001（Database per Service）: 発注執行専有 DB（order_execution_svc）。
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=postgres;Port=5432;Database=order_execution_svc;Username=ai;Password=ai";
builder.Services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseNpgsql(connStr));

builder.Services.AddAiStockTradingHealthChecks()
    .AddNpgSql(connStr, tags: ["ready"]);

// IADR-0016, IADR-0111, #13: ブローカ選択（構成 Broker:Provider × Broker:Environment・既定 paper/sim）。
// 未知の provider / environment・paper と live の矛盾指定は起動時に安全停止（実弾防止・fail-safe は発注抑止側）。
// 選択は合成起点で 1 度だけ解決し、以降は BrokerSelection を参照する（文字列を各所で読み直さない）。
var brokerSelection = BrokerSelection.FromConfiguration(builder.Configuration);

// IADR-0111 閂 0: 実弾（live 階層）は未解禁。OpenD 接続クライアントを構成する前に停止させる
// （＝live を選んでも OpenD への接続も IBrokerAdapter の生成も起きない）。解禁は LiveTradingGate の
// LiveTradingReleased を true にする 1 ファイルの変更に集約され、別 IADR＋IADR-0056 §3 の前提充足を要する。
LiveTradingGate.Ensure(brokerSelection);

// moomoo 選択時は OpenD 接続クライアント（IMoomooTradeClient）を構成し SIMULATE 限定で発注する（実弾を撃たない）。
// #141, IADR-0092: moomoo 時は IMoomooTradeClient を単一インスタンスで DI 共有し、発注アダプタ（IBrokerAdapter）と
// 実照会プローブ（MoomooReservationBrokerProbe）が同一の OpenD 接続を使う（接続を二重化しない）。paper では登録しない。
if (brokerSelection.IsMoomoo)
{
    builder.Services.AddSingleton<IMoomooTradeClient>(sp => new MMApiMoomooTradeClient(
        MoomooBrokerOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MMApiMoomooTradeClient>()));
}
builder.Services.AddSingleton<IBrokerAdapter>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var moomooClient = sp.GetService<IMoomooTradeClient>(); // moomoo 時のみ登録済み
    // FR-10, #821, IADR-0347: S3（代替注文種別）の設定もアダプタへ渡す。
    // **moomoo のときだけ構成を読む**——paper 構成で moomoo の構成検証（TrdEnv 等）を走らせない。
    var alternativeStop = brokerSelection.IsMoomoo
        ? MoomooBrokerOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()).AlternativeStop
        : null;
    return BrokerFactory.Create(
        brokerSelection, moomooClient, loggerFactory.CreateLogger<MoomooBrokerAdapter>(), alternativeStop);
});

builder.Services.AddSingleton<IClock, SystemClock>();
// DbContext が scoped のため発注結果ストアも scoped。
builder.Services.AddScoped<IExecutedOrderStore, EfExecutedOrderStore>();
// #131, IADR-0057: 発注前 DecisionId 予約（二重発注の防止）。
builder.Services.AddScoped<IOrderReservationStore, EfOrderReservationStore>();
// FR-10, #331, IADR-0210: 保護逆指値レグの記録（同時発注の保存とガードの巡回対象）。
builder.Services.AddScoped<IProtectiveStopOrderStore, EfProtectiveStopOrderStore>();
// 🔴 FR-10, FR-05, ADR-0016, #864, IADR-0355: 決済（Close）の発注前に**ブローカーの実建玉と突き合わせる**ため、
// 建玉照会（IBrokerPositionSource）を任意依存として渡す。**実装しない発注先（内蔵 paper）では null のまま**であり、
// 突合そのものが起きない（従来どおり。下の moomoo 限定の登録と対になる構造的な非干渉）。
builder.Services.AddScoped(sp => new OrderExecutionAppService(
    sp.GetRequiredService<IBrokerAdapter>(),
    sp.GetRequiredService<IExecutedOrderStore>(),
    sp.GetRequiredService<IOrderReservationStore>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<IProtectiveStopOrderStore>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<OrderExecutionAppService>(),
    sp.GetService<IBrokerPositionSource>()));

// FR-10, FR-12, ADR-0040 決定1（S1）, #820, IADR-0344 決定9: ソフトウェア逆指値の発動（StopLossTriggered の購読と、ガードの再試行が共有）。
// 🔴 **構成を問わず登録する**——購読ハンドラ（StopLossTriggeredHandler）は規約発見で常に配線され、ビルド時 codegen も
// 既定（内蔵 paper）構成でホストを組むため、依存が解決できないとハンドラが組めない。建玉照会（IBrokerPositionSource）は
// moomoo 構成でだけ登録されており、内蔵 paper では null（決済は据え置き）になる。S1 は内蔵 paper では選べない（行ができない）。
builder.Services.AddScoped(sp => new SoftwareStopExecutor(
    sp.GetRequiredService<IBrokerAdapter>(),
    sp.GetService<IBrokerPositionSource>(),
    sp.GetRequiredService<IProtectiveStopOrderStore>(),
    sp.GetRequiredService<IExecutedOrderStore>(),
    sp.GetRequiredService<IOrderReservationStore>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<SoftwareStopExecutor>()));

// FR-11, FR-16, ADR-0016 決定15, ADR-0027 決定2/決定4, #633, IADR-0300: 取引の経費区分の記録（段 1）。
// 既定の供給口は **常に「取得できない」** を返す no-op であり、経費イベントは 1 本も出ない。
// 出るのは「7 区分すべて未計上（明細 0 件）」を残す警告ログだけである——「照会する経路が無い」と
// 「照会したが取得できない」を区別できるようにするのが段 1 の目的である。
// 実費の取得（moomoo の注文費用照会）は段 2（実口座での応答仕様の確認が前提）。
builder.Services.AddSingleton<IOrderExpenseSource, UnsuppliedOrderExpenseSource>();
builder.Services.AddScoped<TradeExpenseRecordingService>();

// #154, FR-19, #847, #768, IADR-0067, IADR-0357: 注文履歴テレメトリ（訂正・取消の適用＋永続化＋発行）。
//
// 🔴 **取消は全構成で登録する（moomoo を含む）。** 稼働環境（#847）で、板に残った手仕舞いを消す手段が
// moomoo アプリしか無く、利用者が手で取り消すしかなかった。取消そのものは当初から IBrokerAdapter に在り
// moomoo も実装している（ProtectiveStopGuard も OrderExecutionAppService も既に呼んでいる）ため、
// ここを閉じていたことに fail-safe 上の意味は無かった。駆動元は PositionCloseCancellationHandler
//（利用者の OwnerOnly 操作）であり、これが #768「呼び出し元が無い」の解消である。
//
// **訂正（ModifyOrderAsync）はペーパー専用のまま**である（IADR-0067）。実 OpenD へ TrdModifyOrder を
// 配線していないためで、IOrderAmendmentBroker は moomoo 構成では登録せず、OrderAmendmentService は
// それを任意依存として受けて ModifyAsync を NotSupportedException で閉じる（型と実行時の二重の遮断）。
// #141（リコンサイルの取消基点）・#152（pause による強制取消）は依然として未配線である。
builder.Services.AddScoped<IOrderLifecycleStore, EfOrderLifecycleStore>();
if (!brokerSelection.IsMoomoo)
{
    builder.Services.AddSingleton<IOrderAmendmentBroker>(sp =>
        (IOrderAmendmentBroker)sp.GetRequiredService<IBrokerAdapter>());
}

builder.Services.AddScoped<OrderAmendmentService>();
builder.Services.AddScoped<OrderAmendmentDispatcher>();

// 🔴 FR-10, FR-05, #858, IADR-0370, IADR-0350 決定5: 利用者が承認した乖離の取り込み（PositionDriftAdopted）に
// 保護記録とブローカー側の保護注文を追随させる。
// 🔴 **構成を問わず登録する**——購読ハンドラは規約発見で常に配線され、ビルド時 codegen も既定（内蔵 paper）構成で
// ホストを組むため、依存が解決できないとハンドラが組めない（SoftwareStopExecutor と同じ理由）。
// 建玉照会（IBrokerPositionSource）は moomoo 構成でだけ登録されており、内蔵 paper では null
// （取り込みの観測だけを目標にする。IADR-0370 決定3）。
// 🔴 #942, IADR-0395: 業務メトリクス（BusinessMetrics。AddAiStockTradingObservability が登録するシングルトン）を**必ず**渡す。
// 引数は省略可能なので、ここで落とすとコンパイルも既存の試験も通ったまま、再試行を使い切った打ち切りが 1 件も数えられず、
// アラート AstDriftAdoptionFollowUpAbandoned は**エラーを出さずに永久に鳴らない**（T-10-785 が Program.cs そのもので固定する）。
builder.Services.AddScoped(sp => new ProtectiveStopDriftAdopter(
    sp.GetRequiredService<IProtectiveStopOrderStore>(),
    sp.GetRequiredService<OrderAmendmentService>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ProtectiveStopDriftAdopter>(),
    sp.GetService<IBrokerPositionSource>(),
    sp.GetRequiredService<BusinessMetrics>()));

// NFR（運用）, #137, IADR-0059: 予約表の終端行（Completed）の保持期間パージ（既定無効。Retention:Enabled=true で有効化）。
// Reserved（＝発注済みか不明）はどれだけ古くても対象外。滞留の解消は #141 か人手であって時間経過ではない。
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddHostedService<OrderReservationRetentionService>();

// #141, IADR-0074: Reserved 滞留の自動リコンサイル（アプリ既定は無効 Reconciliation:Enabled=false）。
// 🔴 #856, IADR-0362: **配備（deploy/helm/ai-stock-trading/values.yaml）では Enabled / UseBrokerProbe が true** で、
// 解放の門（ReleaseOnNotPlaced）だけを閉じている。ここで既定を反転させないのは、docker-compose・単体開発環境の
// 挙動を変えないためである（有効化は配備の設定点 1 箇所に集める）。
// プローブは差し替え可能で、既定は no-op（常に Indeterminate＝何も解放・終端化しない）。
// #141, IADR-0092: Broker:Provider=moomoo かつ Reconciliation:UseBrokerProbe=true のときだけ実照会プローブ
// （MoomooReservationBrokerProbe・OpenD SIMULATE）を配線する。それ以外（paper／OpenD 無し／既定）は no-op のまま。
// no-op プローブ下では phase-4 自己修復のみ作動し、二重発注を招く解放は構造上起きない。
builder.Services.Configure<ReconciliationOptions>(
    builder.Configuration.GetSection(ReconciliationOptions.SectionName));
if (brokerSelection.IsMoomoo
    && builder.Configuration.GetSection(ReconciliationOptions.SectionName).Get<ReconciliationOptions>()?.UseBrokerProbe == true)
{
    builder.Services.AddSingleton<IReservationBrokerProbe>(sp => new MoomooReservationBrokerProbe(
        sp.GetRequiredService<IMoomooTradeClient>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MoomooReservationBrokerProbe>()));
}
else
{
    builder.Services.AddSingleton<IReservationBrokerProbe, IndeterminateReservationBrokerProbe>();
}
builder.Services.AddScoped<OrderReservationReconciler>();
builder.Services.AddHostedService<OrderReservationReconciliationService>();

// #270, FR-10, IADR-0113: 約定状態の追跡ポーリング（既定有効・短周期）。moomoo は発注時に Accepted（未約定）を
// 返すため、これが無いと約定が取引台帳へ届かず統制上限（SameDayReentry・日次発注上限・段階資金上限）が実効しない。
// paper は即時終端で非終端の記録が生まれないため、配線を moomoo 選択時に限定する（構造的な非干渉）。
builder.Services.Configure<FillPollingOptions>(builder.Configuration.GetSection(FillPollingOptions.SectionName));
if (brokerSelection.IsMoomoo)
{
    // 🔴 FR-10, #833 項目1, IADR-0389: 受理だけで完了させた S1 の保護記録の再武装。約定追跡が「確認できた
    // 終端かつ未約定」を観測したときだけ働く（新しい常駐は足さない）。据え置き（照会不能）の通知の記憶は
    // 巡回をまたぐため singleton・非永続（再起動後の最初の巡回で必ず鳴る）。
    builder.Services.AddSingleton<UnresolvedCloseNotificationTracker>();
    builder.Services.AddScoped(sp => new SoftwareStopReArmer(
        sp.GetRequiredService<IProtectiveStopOrderStore>(),
        sp.GetRequiredService<IExecutedOrderStore>(),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<SoftwareStopReArmer>(),
        sp.GetRequiredService<UnresolvedCloseNotificationTracker>()));
    builder.Services.AddScoped(sp => new OrderFillPoller(
        sp.GetRequiredService<IBrokerAdapter>(),
        sp.GetRequiredService<IExecutedOrderStore>(),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<SoftwareStopReArmer>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<OrderFillPoller>()));
    builder.Services.AddHostedService<OrderFillPollingService>();
}

// #292, FR-05, FR-10, IADR-0118: ブローカ実ポジションの定期観測（既定有効）。突合はリスク管理が行う。
// 建玉照会を実装するアダプタ（IBrokerPositionSource）がある構成でのみ配線する。paper は実装しないため
// 常駐そのものが登録されず、1 度も照会が起きない（構造的な非干渉）。
builder.Services.Configure<PositionReconciliationOptions>(
    builder.Configuration.GetSection(PositionReconciliationOptions.SectionName));
if (brokerSelection.IsMoomoo)
{
    builder.Services.AddSingleton<IBrokerPositionSource>(sp =>
        (IBrokerPositionSource)sp.GetRequiredService<IBrokerAdapter>());
    // TimeProvider は既定では DI に登録されないため明示的に入れる（観測時刻の供給元）。
    builder.Services.AddSingleton(TimeProvider.System);
    // 🔴 FR-10, #880, IADR-0412 決定1: 建玉観測の常駐が同じスナップショットで帰属不明の建玉も検知する（相乗り。照会は増やさない）。
    // 巡回ごとのスコープで解決する（EF のストアが scoped）。Active の読み出し件数はガードの巡回と同じ上限を使う。
    builder.Services.AddScoped(sp => new UnattributedPositionDetector(
        sp.GetRequiredService<IProtectiveStopOrderStore>(),
        sp.GetRequiredService<IExecutedOrderStore>(),
        sp.GetRequiredService<IClock>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProtectiveStopGuardOptions>>().Value.BatchSize));
    builder.Services.AddHostedService<BrokerPositionSnapshotService>();
}

// FR-10, UC-02, #331, IADR-0210 決定4: 保護逆指値ガード（失効検知・再発注・残存取消。既定有効）。
// 判定の前提（ブローカー注文照会＋建玉照会 IBrokerPositionSource）を持つ moomoo 構成でのみ配線する。
// paper は建玉照会を実装しないため常駐そのものを登録しない（構造的な非干渉。分岐は単体テストで固定）。
builder.Services.Configure<OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.ProtectiveStopGuardOptions>(
    builder.Configuration.GetSection(
        OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.ProtectiveStopGuardOptions.SectionName));
if (brokerSelection.IsMoomoo)
{
    // #848, IADR-0117（改定 9）: 据え置き中の成行手仕舞いを「このプロセスがいつ通知したか」の記憶（singleton・非永続）。
    // ガードは巡回ごとに作られる scoped なので、記憶は外に置く。再起動で消えることが「再起動後に必ず再通知する」仕組み。
    builder.Services.AddSingleton<
        OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.HeldCloseNotificationTracker>();
    // #857, IADR-0369: 「確認できた拒否」で終わった成行手仕舞いの数え（撃ち直しの上限）と再通知の記憶
    //（singleton・非永続。再起動で数えが消える＝もう一度手仕舞いを試みる側へ倒れる）。
    builder.Services.AddSingleton<
        OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.CloseRejectionTracker>();
    builder.Services.AddScoped<OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.ProtectiveStopGuard>(sp =>
        new OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.ProtectiveStopGuard(
            sp.GetRequiredService<IBrokerAdapter>(),
            (IBrokerPositionSource)sp.GetRequiredService<IBrokerAdapter>(),
            sp.GetRequiredService<IProtectiveStopOrderStore>(),
            sp.GetRequiredService<IExecutedOrderStore>(),
            // #848, IADR-0117（改定 7）: 成行手仕舞いも予約 → 発注 → 確定の 3 相で送る（届いたか不明を撃ち直さない）。
            sp.GetRequiredService<IOrderReservationStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger<OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.ProtectiveStopGuard>(),
            sp.GetRequiredService<
                OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.HeldCloseNotificationTracker>(),
            // #820, IADR-0344 決定6: 到達済み S1 行の決済再試行はガードが実行器へ委ねる。
            sp.GetRequiredService<SoftwareStopExecutor>(),
            // #857, IADR-0369: 確認できた拒否の数え（撃ち直しの上限）。
            sp.GetRequiredService<
                OrderExecutionService.Features.OrderExecution.GuardProtectiveStops.CloseRejectionTracker>()));
    // FR-10, #902, IADR-0365 決定5: Active な S1 行の低頻度の要約（観測のみ。間隔をまたいで状態を持つため singleton）。
    builder.Services.AddSingleton(sp =>
        new SoftwareStopLivenessReporter(
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SoftwareStopLivenessReporter>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProtectiveStopGuardOptions>>()
                .Value.SoftwareStopSummaryInterval));
    builder.Services.AddHostedService<
        OrderExecutionService.Hosted.ProtectiveStopGuardService>();
}

// FR-20, FR-05, #385, 06_daytrading-review §4.2, IADR-0150: ブローカ稼働の定期観測（既定有効）。
// Stage 1 の「その日の実際の通常取引時間の 50% 以上が稼働していること」を数えるための唯一の供給元であり、
// 発行された観測をリスク管理が米国東部時間の取引日ごとに積む。副作用は読み取り照会のみで発注を増やさない。
//
// **paper 構成でも配線する。** 内蔵 paper で稼働した日は算入されない（許可制・IADR-0142 決定2）が、
// 「paper 稼働により N 日を除外」と別掲する（SC-03）ための観測はそこでしか得られない。
builder.Services.Configure<BrokerAvailabilityProbeOptions>(
    builder.Configuration.GetSection(BrokerAvailabilityProbeOptions.SectionName));
builder.Services.AddSingleton<IBrokerAvailabilityProbe>(sp =>
    (IBrokerAvailabilityProbe)sp.GetRequiredService<IBrokerAdapter>());
// FR-19, #375, ADR-0021 決定3, IADR-0153: 口座種別の供給元。**実装しているアダプタのときだけ登録する**——
// 内蔵 paper は外部へ一度も発注せず、ブローカー口座そのものが存在しない（口座種別を観測しようがない）。
// 未登録なら probe は口座種別を発行せず、リスク管理側は moomoo 発注先の新規建てを止める（フェイルクローズ）。
if (brokerSelection.IsMoomoo)
{
    builder.Services.AddSingleton<IBrokerAccountSource>(sp =>
        (IBrokerAccountSource)sp.GetRequiredService<IBrokerAdapter>());
}
// TimeProvider は既定では DI に登録されないため明示的に入れる（観測時刻の供給元）。moomoo 構成では上で登録済み。
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddHostedService<BrokerAvailabilityProbeService>();

// ADR-0013, IADR-0129, #354: Wolverine（RabbitMQ）。OrderApproved を購読し発注、OrderExecuted を発行する。
// ハンドラは明示登録ではなくアセンブリ走査で発見されるため、ハンドラを持つアセンブリ（Infrastructure）を明示する。
// キュー名・fan-out・再試行・DLQ の規則は共通ヘルパに閉じている（サービス側でトポロジを選ばない）。
// **IADR-0129 決定 3 が効く経路**: OrderApproved は発行元の RiskManagementService 自身も購読しており、
// ローカルルーティングを無効化しないと承認済みの発注が本サービスへ一通も届かない。
builder.Host.UseWolverine(opts => opts.UseAiStockTradingRabbitMq(
    ServiceName,
    builder.Configuration["RabbitMq:ConnectionString"],
    typeof(OrderApprovedHandler).Assembly));

// ADR-0001, FR-15, #22 受け入れ基準③: 実効構成（有効な段=宣言由来・選択中ポート実装・構成バージョン）の自己申告。
// メッシュ内部限定エンドポイント GET /internal/introspection（無認可・ネットワーク分離が防御）。
// IADR-0111: 自己申告は正準名 Tier（paper ＜ moomoo-sim ＜ moomoo-live＝本番近接順）で行う。
// 生の Broker:Provider だけでは取引環境（シム／実弾）が判らず「今どの階層か」を実行時に確認できない。
builder.Services.AddAiStockTradingIntrospection(builder.Configuration, ServiceName, b => b.AddPort("broker", brokerSelection.Tier));

var app = builder.Build();

// 🔴 FR-10, #942, IADR-0395: 追随の打ち切りのカウンタを**起動完了後に 0 で計上して系列を先に作る**。
// 最初の打ち切りで系列が初めて現れると、Prometheus の increase() はその 1 点目を数えず、起動後の最初の打ち切りを
// アラートが取りこぼす。ApplicationStarted を待つのは、OTel の MeterProvider（ホストの開始時に立つ）より前の計上が
// 誰にも聞かれずに消えるためである（T-10-786 が OTel の exporter まで届くことを Program.cs そのもので固定する）。
app.Lifetime.ApplicationStarted.Register(() =>
    app.Services.GetRequiredService<BusinessMetrics>().PrimeDriftAdoptionFollowUpAbandoned());

// 起動時にスキーマを最新 Migration へ更新（relational のみ。テストの InMemory はスキップ）。
// #811 / IADR-0129 追記: `codegen write` 等の JasperFx コマンドで起動したときは DB に触らない（ホスト稼働時だけ移行する）。
if (JasperFxCommandLine.IsHostRun(args))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrderExecutionDbContext>();
    if (db.Database.IsRelational())
        await db.Database.MigrateAsync();
}

app.MapAiStockTradingHealthChecks();
app.MapAiStockTradingIntrospection();

// #811 / IADR-0129 追記: 全サービス共通の終端（shim）。JasperFx のコマンドライン（`dotnet <dll> codegen write` 等）を受け、引数なしは従来の app.Run と同じ稼働。
return await app.RunAiStockTradingAsync(args);

// 統合テスト（WebApplicationFactory）が参照するためのエントリポイント公開。
public partial class Program { }
