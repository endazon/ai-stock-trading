using OrderExecutionService.Domain;
using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Features.OrderExecution.AdoptPositionDrift;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using OrderExecutionService.Infrastructure.Persistence;
using System.Diagnostics.Metrics;
using System.Reflection;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.TestSupport.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-740・T-10-741・T-10-785・T-10-786, FR-10, FR-05, #858, #942, IADR-0370（2026-09-24 追記 / PR #918 監査）, IADR-0395:
// **本番の Program.cs の組み立て**が、moomoo 構成でだけ ProtectiveStopDriftAdopter へ建玉照会（IBrokerPositionSource）を渡すことを固定する。
//
// 追随の規律（照会が不明・失敗なら保護を 1 つも変えない）は建玉照会が注入されているときにしか働かない。
// 単体の試験（ProtectiveStopDriftAdopterTests）は照会を自分で渡し、ハンドラの試験（PositionDriftAdoptedHandlerTests）は
// 自前の DI で型ごと登録するため、**Program.cs が null を渡しても両方とも通る**（PR #918 の監査で実測した生存変異）。
// そうなると本番の moomoo 構成では最大 60 分古い取り込みの観測だけで保護逆指値を取り消し、実在する建玉が無保護になり得る。
//
// ここでは WebApplicationFactory で Program.cs そのものを組み、差し替えるのは外界（ブローカー・DB・常駐）だけにする。
// 建玉照会は Program.cs 自身が「IBrokerAdapter を IBrokerPositionSource へ変換する」登録で作るため、
// 偽のアダプタが建玉照会も実装していれば、Program.cs の配線をそのまま通って届く。
public class ProtectiveStopDriftAdopterCompositionTests
{
    // 建玉照会は「不明」（null）を返す。取消後の照会は Cancelled（取り消せた）を返す。
    // 内蔵 paper 構成の Program.cs はアダプタを IOrderAmendmentBroker へ変換して登録するため、それも実装する（訂正は使わない）。
    private sealed class ScriptedBroker : IBrokerAdapter, IBrokerPositionSource, IOrderAmendmentBroker
    {
        public BrokerProvider Provider => BrokerProvider.MoomooSimulate;

        public int PositionQueries { get; private set; }

        public int CancelCount { get; private set; }

        public Task<BrokerOrder> PlaceOrderAsync(OrderIntent intent, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは発注しない");

        public Task<BrokerOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult<BrokerOrder?>(new BrokerOrder(
                orderId, StopIntent(), OrderStatus.Cancelled, 0, 0m, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        public Task<BrokerOrder> ModifyOrderAsync(
            string orderId, int quantity, decimal price, CancellationToken ct = default) =>
            throw new NotSupportedException("本テストは訂正しない");

        public Task CancelOrderAsync(string orderId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BrokerPositionSnapshot>?> GetPositionsAsync(CancellationToken ct = default)
        {
            PositionQueries++;
            return Task.FromResult<IReadOnlyList<BrokerPositionSnapshot>?>(null);
        }
    }

    // Program.cs を指定の発注先で組む。外界だけを差し替える（ブローカー・DB・自前の常駐）。
    private sealed class ProgramFactory(
        string provider, ScriptedBroker broker, Action<IServiceCollection>? observe = null) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // 🔴 発注先の選択は Program.cs の最上段（builder.Build() の前）で読まれるため、
            // ホスト設定（UseSetting）で渡す。ConfigureAppConfiguration では分岐に間に合わない。
            builder.UseSetting("Broker:Provider", provider);
            builder.UseSetting("Broker:Environment", "sim");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");

            builder.ConfigureServices(services =>
            {
                // 外界 1: ブローカー。Program.cs の IBrokerAdapter 登録だけを置き換え、
                // IBrokerPositionSource の登録（moomoo 構成のときだけ IBrokerAdapter を変換する）は Program.cs のまま通す。
                services.RemoveAll<IBrokerAdapter>();
                services.AddSingleton<IBrokerAdapter>(broker);

                // 外界 2: DB（InMemory）。
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<OrderExecutionDbContext>)
                             || (d.ServiceType.IsGenericType
                                 && d.ServiceType.GetGenericTypeDefinition().FullName?
                                     .Contains("IDbContextOptionsConfiguration") == true
                                 && d.ServiceType.GenericTypeArguments.Length == 1
                                 && d.ServiceType.GenericTypeArguments[0] == typeof(OrderExecutionDbContext)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
                services.AddDbContext<OrderExecutionDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

                // 外界 3: 自前の常駐（ガード・約定追跡・建玉観測など）。偽のブローカーを巡回で叩かせない。
                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                services.DisableAllExternalWolverineTransports();

                // 観測の口だけを足す（#942: OTel の exporter を 1 つ足す）。Program.cs の登録は置き換えない。
                observe?.Invoke(services);
            });
        }
    }

    private static OrderIntent StopIntent() =>
        new("AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash, BrokerProvider.MoomooSimulate,
            10, 950m, PositionEffect.Close);

    private static ProtectiveStopOrder SeedStop(IServiceProvider services)
    {
        var now = DateTimeOffset.UtcNow;
        var entryDecisionId = Guid.NewGuid();
        var stopDecisionId = ProtectiveStopIds.StopDecisionId(entryDecisionId, attempt: 1);
        var stop = new ProtectiveStopOrder(
            entryDecisionId, stopDecisionId, "stop-1", "AAPL", Market.UnitedStates, TradeSide.Buy,
            ProductType.Cash, BrokerProvider.MoomooSimulate, 10, 950m, 1m, Attempt: 1,
            ProtectiveStopState.Active, now.AddMinutes(-30), now.AddMinutes(-30), RemainingProtected: 10);
        services.GetRequiredService<IProtectiveStopOrderStore>().Save(stop);
        services.GetRequiredService<IExecutedOrderStore>().Save(new ExecutionRecord(
            stopDecisionId, "stop-1", "AAPL", Market.UnitedStates, TradeSide.Sell, ProductType.Cash,
            PositionEffect.Close, 10, 950m, 0, 0m, OrderStatus.Accepted, 0m, now.AddMinutes(-30)));
        return stop;
    }

    private static PositionDriftAdopted Adopted() =>
        new(Guid.NewGuid(), "AAPL", Market.UnitedStates, 10, 0, 0, DateTimeOffset.UtcNow.AddMinutes(-5), 1_000m,
            RealizedPnlRecorded: false, ReferencePrice: null, EstimatedPnlInBase: null,
            Actor: "owner", Reason: "証券会社のアプリで全株売却", AdoptedAt: DateTimeOffset.UtcNow);

    // ---- T-10-740: moomoo 構成では Program.cs が建玉照会を渡す（不明なら保護を変えずに投げる） ----
    // 🔴 変異「Program.cs で建玉照会の代わりに null を渡す」はこの試験だけが殺す（照会が 1 度も起きず、取消が走る）。
    [Fact]
    public async Task moomoo構成ではProgramが建玉照会を渡し_照会が不明なら保護を取り消さずに投げる()
    {
        var broker = new ScriptedBroker();
        await using var factory = new ProgramFactory("moomoo", broker);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<IBrokerPositionSource>().Should()
            .BeSameAs(broker, "前提: moomoo の分岐を通り、建玉照会はアダプタから作られている");
        var stop = SeedStop(scope.ServiceProvider);
        var adopter = scope.ServiceProvider.GetRequiredService<ProtectiveStopDriftAdopter>();

        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(() => adopter.ApplyAsync(Adopted()));

        broker.PositionQueries.Should().Be(1, "Program.cs が建玉照会を渡していれば、取り消す前に 1 度照会する");
        broker.CancelCount.Should().Be(0, "建玉が消えたと確かめられないまま保護を取り消さない");
        var after = scope.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(stop.EntryDecisionId)!;
        after.State.Should().Be(ProtectiveStopState.Active);
        after.RemainingProtected.Should().Be(10);
    }

    // ---- T-10-741: 内蔵 paper 構成では建玉照会を渡さない（取り込みの観測に従う・対の肯定形） ----
    // 同じ偽のアダプタ（建玉照会も実装している）でも、Program.cs が paper で IBrokerPositionSource を登録しないので照会は起きない。
    // T-10-740 の差が「偽のアダプタの形」ではなく **Program.cs の構成分岐**から来ることを示す。
    [Fact]
    public async Task paper構成ではProgramが建玉照会を渡さず_取り込みの観測に従って取り消す()
    {
        var broker = new ScriptedBroker();
        await using var factory = new ProgramFactory("paper", broker);
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetService<IBrokerPositionSource>().Should()
            .BeNull("前提: 内蔵 paper は建玉照会を登録しない");
        var stop = SeedStop(scope.ServiceProvider);
        var adopter = scope.ServiceProvider.GetRequiredService<ProtectiveStopDriftAdopter>();

        var result = await adopter.ApplyAsync(Adopted());

        broker.PositionQueries.Should().Be(0);
        broker.CancelCount.Should().Be(1, "建玉照会を持たない構成は取り込みの観測を目標にする（IADR-0370 決定3）");
        result.Events.OfType<SoftwareStopExecuted>().Should().ContainSingle()
            .Which.Outcome.Should().Be(SoftwareStopOutcome.ProtectionReduced);
        scope.ServiceProvider.GetRequiredService<IProtectiveStopOrderStore>().Find(stop.EntryDecisionId)!
            .State.Should().Be(ProtectiveStopState.Completed);
    }

    // ---- T-10-785: Program.cs が業務クラスへ DI のシングルトンの BusinessMetrics を渡し、最後の配送の打ち切りを数える ----
    // 🔴 #942, IADR-0395: 引数は省略可能なので、Program.cs が渡し忘れてもコンパイルも他の試験も通る。そのときアラート
    // AstDriftAdoptionFollowUpAbandoned は**エラーを出さずに永久に鳴らない**（PR #919 / #918 の監査が実測した形と同じ）。
    // 殺す変異: ①Program.cs で BusinessMetrics を渡さない（保持が null）②BusinessMetrics の登録を消す（解決が落ちる）。
    [Fact]
    public async Task ProgramはBusinessMetricsのシングルトンを業務クラスへ渡し_最後の配送の打ち切りを数える()
    {
        using var capture = new MeterCapture(BusinessMetricNames.MeterName);
        var broker = new ScriptedBroker();
        await using var factory = new ProgramFactory("moomoo", broker);
        using var scope = factory.Services.CreateScope();
        var singleton = scope.ServiceProvider.GetRequiredService<BusinessMetrics>();
        SeedStop(scope.ServiceProvider);
        var adopter = scope.ServiceProvider.GetRequiredService<ProtectiveStopDriftAdopter>();

        var held = typeof(ProtectiveStopDriftAdopter)
            .GetField("_metrics", BindingFlags.Instance | BindingFlags.NonPublic);
        held.Should().NotBeNull("業務クラスは業務メトリクスをフィールドで保持する");
        held!.GetValue(adopter).Should().BeSameAs(singleton, "Program.cs は DI のシングルトン（OTel が載せている Meter）を渡す");

        var adopted = Adopted();
        await Assert.ThrowsAsync<ProtectiveStopDriftPositionsUnknownException>(
            () => adopter.ApplyAsync(adopted, finalDeliveryAttempt: true));

        // 肯定形だけを書く（既定の Meter 名はプロセス全体で観測される。否定形は単体試験 T-10-782 が隔離して持つ）。
        capture.ValuesOf(BusinessMetricNames.DriftAdoptionFollowUpAbandoned).Should().Contain(m =>
            m.Value == 1 && m.Tags[BusinessMetricNames.TagReason] == BusinessMetrics.DriftFollowUpPositionsUnknown);
        broker.CancelCount.Should().Be(0);
    }

    // ---- T-10-786: Program.cs が起動完了後に打ち切りのカウンタを 0 で計上し、それが OTel の exporter まで届く ----
    // 🔴 #942, IADR-0395: 系列が最初の打ち切りで初めて現れると、Prometheus の increase() はその 1 点目を数えない
    //（起動後の最初の打ち切りをアラートが取りこぼす）。0 は **MeterProvider が立った後**に計上しないと誰にも聞かれない。
    // 殺す変異: ①起動時の計上を消す ②計上を ApplicationStarted より前（ホストの開始前）へ動かす。
    //
    // 🔴 IADR-0395（2026-09-25 追記）: 本試験は並列・高負荷で赤（exporter が空）になることがあった。原因は本番ではなく
    // **試験側の 2 つの競走**である。1 つ目は同期:WebApplicationFactory の `factory.Services` は、ホストの ApplicationStarted が**発火した瞬間**に
    // 戻る（ファクトリ側の登録が発火を受けて待ちを解くだけで、Program.cs が同じトークンへ登録したコールバックの
    // **完了は待たない**。トークンのコールバックは後に登録したものから走るため、ファクトリの解放が Program.cs の計上より
    // 先に来る）。試験のスレッドが ForceFlush まで進むのと、起動のスレッドが 0 を計上するのとが競走し、負荷で前者が勝つと
    // exporter に点が 1 つも届かない。**計上そのものを見届けてから**吐き出させる（下の PrimeLatch）。
    // 2 つ目は期限: MeterProvider.ForceFlush(10_000) の期限を先に回る OTLP の reader が食い切ると、足した reader が export
    // されずに同じ文面で赤になる（下の reader.Collect() のコメント）。足した reader だけを期限なしで吐き出させる。
    // あわせて Meter 名を試験ごとに隔離する。既定名はプロセス全体で共有され、並走する別の Program ホストの 0 が
    // この試験の provider にも届くため、「自分のホストの計上を見届けた」も「exporter に届いた」も他人の計上で満たされ得る。
    // 差し替えるのは BusinessMetrics の Meter 名だけで、計上の呼び出し（Program.cs の ApplicationStarted）は本番のまま通す。
    [Theory]
    [InlineData("paper")]
    [InlineData("moomoo")]
    public async Task Programは起動完了後に打ち切りのカウンタを0で計上し_OTelのexporterまで届く(string provider)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var primed = new PrimeLatch(meterName);
        var exported = new List<(string Name, long Value, string? Reason)>();
        var reader = new BaseExportingMetricReader(new SumCapturingExporter(exported));
        await using var factory = new ProgramFactory(provider, new ScriptedBroker(), services =>
        {
            services.RemoveAll<BusinessMetrics>();
            services.AddSingleton(_ => BusinessMetrics.WithMeterName(meterName));
            services.ConfigureOpenTelemetryMeterProvider(b => b.AddMeter(meterName).AddReader(reader));
        });

        _ = factory.Services; // ホストを開始する（ApplicationStarted が発火する。コールバックの完了は待たない）。

        // 起動のスレッドが 0 を計上し終えるまで待つ（再試行ではない。計上という 1 つの出来事を待つ）。
        // 変異①（計上を消す）はここで落ちる。変異②（開始前へ動かす）はここを通り、下の exporter の表明で落ちる。
        primed.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "Program.cs は起動完了の通知で 2 つの理由の系列を 0 で計上する");

        // 🔴 足した reader だけを期限なしで吐き出させる。MeterProvider.ForceFlush(期限) は reader を登録順に回し、
        // 残り時間を次へ渡す——先に回る OTLP の reader（otel-collector が居ないので失敗する送信）が高負荷で期限を食うと、
        // 足した reader は残り 0 で「集めたが export しない」（OTel 1.16 MetricReader.ProcessMetricsCollection）になり、
        // exporter が空のまま赤になる（実測: 高負荷で ForceFlush 1 回に最大 4.7 秒、期限 1 ms で決定的に空）。
        reader.Collect().Should().BeTrue("足した exporter は常に成功を返す（OTLP の送信の成否とは切り離す）");

        var points = exported.Where(e => e.Name == BusinessMetricNames.DriftAdoptionFollowUpAbandoned).ToList();
        points.Select(p => p.Reason).Should().Contain(
            [BusinessMetrics.DriftFollowUpPositionsUnknown, BusinessMetrics.DriftFollowUpPositionsQueryFailed],
            "起動しただけで 2 つの理由の系列が既に在る（0 から始まるので最初の打ち切りを increase() が拾える）");
        points.Should().OnlyContain(p => p.Value == 0, "起動しただけでは打ち切りは 1 件も起きていない");
    }

    /// <summary>
    /// 隔離した Meter 名の打ち切りのカウンタに、2 つの理由の計上が両方とも届いたら開く門（T-10-786）。
    /// OTel とは独立の <see cref="MeterListener"/> で、ホストの開始より前から聞く。
    /// </summary>
    private sealed class PrimeLatch : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly HashSet<string> _reasons = [];
        private readonly ManualResetEventSlim _both = new();

        public PrimeLatch(string meterName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName
                    && instrument.Name == BusinessMetricNames.DriftAdoptionFollowUpAbandoned)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key != BusinessMetricNames.TagReason || tag.Value is not string reason) continue;
                    lock (_reasons)
                    {
                        _reasons.Add(reason);
                        if (_reasons.Contains(BusinessMetrics.DriftFollowUpPositionsUnknown)
                            && _reasons.Contains(BusinessMetrics.DriftFollowUpPositionsQueryFailed))
                        {
                            _both.Set();
                        }
                    }
                }
            });
            _listener.Start();
        }

        public bool Wait(TimeSpan timeout) => _both.Wait(timeout);

        public void Dispose()
        {
            _listener.Dispose();
            _both.Dispose();
        }
    }

    /// <summary>export された long の合計値を（計器名・値・reason タグ）で集める最小の exporter。</summary>
    private sealed class SumCapturingExporter(List<(string Name, long Value, string? Reason)> sink) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.LongSum) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    string? reason = null;
                    foreach (var tag in point.Tags)
                    {
                        if (tag.Key == BusinessMetricNames.TagReason) reason = tag.Value?.ToString();
                    }

                    lock (sink) sink.Add((metric.Name, point.GetSumLong(), reason));
                }
            }

            return ExportResult.Success;
        }
    }
}
