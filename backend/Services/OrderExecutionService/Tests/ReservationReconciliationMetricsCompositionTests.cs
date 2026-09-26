using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using OrderExecutionService.Infrastructure.Persistence;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.TestSupport.Metrics;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace OrderExecutionService.Tests;

// 🔴 T-10-1562, FR-05, NFR-09, #856, IADR-0441: **本番の Program.cs** が、発注予約の自動リコンサイルが有効な構成
// （`Reconciliation:Enabled=true`。配備の values.yaml と同じ）でだけ、起動完了後に判定の内訳 6 系列を 0 で先に作ることを固定する。
//
// 系列が最初の 1 件で初めて現れると Prometheus の increase() はその 1 点目を数えない（IADR-0395 と同じ理由）。
// 無効な構成で作らないのは、0 の系列が「巡回して 0 件だった」と読めるためである。
//
// 待ち方: 同じ ApplicationStarted に、先に追随の打ち切りのカウンタの 0（IADR-0395）が登録されている。
// トークンのコールバックは後に登録したものから走るため、**追随の打ち切りの 0 が届いた時点で、リコンサイルの 0 の判断は済んでいる**。
// それを 1 つの出来事として待ち（再試行ではない）、有効なら 6 系列が在ること、無効なら 1 つも無いことを見る。
// Meter 名は試験ごとに隔離する（既定名はプロセス全体で共有され、並走する別の Program ホストの計上が混ざる）。
//
// 殺す変異: ①起動時の計上を消す（有効で赤）②Enabled を見ずに常に計上する（無効で赤）。
public class ReservationReconciliationMetricsCompositionTests
{
    private sealed class ProgramFactory(bool reconciliationEnabled, string meterName) : WebApplicationFactory<Program>
    {
        private readonly string _dbName = Guid.NewGuid().ToString();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Broker:Provider", "paper");
            builder.UseSetting("RabbitMq:ConnectionString", "amqp://localhost");
            builder.UseSetting("Otlp:Endpoint", "http://localhost:4317");
            builder.UseSetting("Reconciliation:Enabled", reconciliationEnabled ? "true" : "false");

            builder.ConfigureServices(services =>
            {
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

                // 自前の常駐（リコンサイルの巡回を含む）は走らせない。見るのは Program.cs の起動時の計上だけである。
                var ownHosted = services
                    .Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType?.Assembly == typeof(Program).Assembly)
                    .ToList();
                foreach (var d in ownHosted) services.Remove(d);

                services.DisableAllExternalWolverineTransports();

                // 差し替えるのは Meter 名だけ（計上の呼び出しは Program.cs のまま通す）。
                services.RemoveAll<BusinessMetrics>();
                services.AddSingleton(_ => BusinessMetrics.WithMeterName(meterName));
            });
        }
    }

    // 隔離した Meter 名の計上を聞き、追随の打ち切りの 0（2 つの理由）が届いたら開く門。ホストの開始より前から聞く。
    private sealed class StartupLatch : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<string> _driftReasons = [];
        private readonly ManualResetEventSlim _driftPrimed = new();

        public StartupLatch(string meterName)
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
                    _driftReasons.Add(reason);
                    if (_driftReasons.Contains(BusinessMetrics.DriftFollowUpPositionsUnknown)
                        && _driftReasons.Contains(BusinessMetrics.DriftFollowUpPositionsQueryFailed))
                    {
                        _driftPrimed.Set();
                    }
                }
            });
            _listener.Start();
        }

        public bool Wait(TimeSpan timeout) => _driftPrimed.Wait(timeout);

        public void Dispose()
        {
            _listener.Dispose();
            _driftPrimed.Dispose();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Programはリコンサイルが有効な構成でだけ起動完了後に判定の内訳を0で作る(bool enabled)
    {
        var meterName = MeterCapture.NewIsolatedMeterName();
        using var capture = new MeterCapture(meterName);
        using var latch = new StartupLatch(meterName);
        await using var factory = new ProgramFactory(enabled, meterName);

        _ = factory.Services; // ホストを開始する（ApplicationStarted が発火する。コールバックの完了は待たない）。

        latch.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "前提: Program.cs は起動完了の通知で追随の打ち切りの 2 系列を 0 で計上する（この後にリコンサイルの判断は済んでいる）");

        var outcomes = capture.TagValuesOf(
            BusinessMetricNames.OrderReservationReconciliations, BusinessMetricNames.TagOutcome);
        if (enabled)
        {
            outcomes.Should().BeEquivalentTo(
                [
                    BusinessMetrics.ReservationReconciliationProbePlaced,
                    BusinessMetrics.ReservationReconciliationSelfHealed,
                    BusinessMetrics.ReservationReconciliationHeldNotPlaced,
                    BusinessMetrics.ReservationReconciliationReleased,
                    BusinessMetrics.ReservationReconciliationIndeterminate,
                    BusinessMetrics.ReservationReconciliationFailed,
                ],
                "有効な構成では起動しただけで 6 系列が在る（0 から始まるので最初の 1 件を increase() が拾える）");
            capture.SumOf(BusinessMetricNames.OrderReservationReconciliations).Should().Be(0, "起動しただけでは判定は 1 件も無い");
        }
        else
        {
            outcomes.Should().BeEmpty("無効な構成で 0 の系列を作ると、巡回して 0 件だったと読める");
        }
    }
}
