using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace AiStockTrading.TestSupport.PlatformShim.Tests;

// NFR-07, #287, IADR-0255: 業務メトリクスの**配線**（composition root）を固定する。
//
// 🔴 **計器を定義しただけでは 1 系列も出ない。** 出るには 2 つの配線が要る:
//   (1) BusinessMetrics が DI に登録されていること（ハンドラの必須依存であり、無ければ起動時に壊れる）
//   (2) Meter が OTel のメトリクスパイプラインへ AddMeter で登録されていること
//       —— **こちらが消えても何も壊れない。記録は続き、外へ出るものだけが静かに消える。**
// そのため (2) は肯定形（opt-in すれば確かに出ていく）と否定形（AddMeter が無ければ出ない）を対で固定する。
// 否定形だけでは「そもそも export の仕組みが動いていない」ときにも緑になる。
public class BusinessMetricsWiringTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    // 配線 (1): 11 サービスが等しく通る唯一の可観測性配線が BusinessMetrics を供給する。
    // ここが欠けると、ハンドラの必須依存が解決できずメッセージ処理が起動時に失敗する。
    [Fact]
    public void 可観測性の登録で_BusinessMetrics_が解決できる()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAiStockTradingObservability(EmptyConfig(), "risk-management-service");
        using var provider = services.BuildServiceProvider();

        provider.GetService<BusinessMetrics>().Should().NotBeNull();
    }

    // 配線 (1) の続き: シングルトンであること。要求のたびに新しい Meter が生まれると、
    // 計器がプロセス内で重複し、リソースの解放も曖昧になる。
    [Fact]
    public void BusinessMetrics_はシングルトンとして供給される()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAiStockTradingObservability(EmptyConfig(), "risk-management-service");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<BusinessMetrics>()
            .Should().BeSameAs(provider.GetRequiredService<BusinessMetrics>());
    }

    // 配線 (2) 肯定形: **opt-in（リーダを付けた構成）では業務メトリクスが確かに出ていく。**
    // 既定の経路B は otel-collector の exporter が debug（標準出力のみ・外部送信なし）であり、
    // 「計装は有効だが外部へは送らない」を collector 側で担保する（IADR-0094 の作法）。
    // ここで固定するのは「送る先を用意すれば、業務メトリクスがその先まで到達する」ことである。
    [Fact]
    public void 業務メトリクスは_OTel_のパイプラインを通って出ていく()
    {
        var exported = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingObservability(EmptyConfig(), "risk-management-service");
        services.ConfigureOpenTelemetryMeterProvider(builder =>
            builder.AddReader(new BaseExportingMetricReader(new CapturingExporter(exported))));

        using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        provider.GetRequiredService<BusinessMetrics>().RecordInformationCollected(3);

        // 🔴 戻り値は見ない。本構成には OTLP exporter も載っており、テスト環境には
        // otel-collector が居ないため ForceFlush は false を返す（**外部送信の失敗は想定内**）。
        // 見るべきは「業務メトリクスが exporter まで到達したか」であって、全 exporter の成否ではない。
        meterProvider.ForceFlush(10_000);

        exported.Should().Contain(
            BusinessMetricNames.InformationItemsCollected,
            "AddMeter が効いていれば、業務メトリクスは exporter まで到達する");
    }

    // 配線 (2) 否定形: **AddMeter を含まない構成では出ていかない。**
    // これが無いと、上の肯定形は「AddMeter の行を消しても緑」になり得る
    // （既製の計装が出ていることを見ているだけになる）。本テストは AddMeter の行が
    // **載せているのはまさにこの Meter である**ことを示す。
    [Fact]
    public void AddMeter_を含まない構成では業務メトリクスは出ていかない()
    {
        var exported = new List<string>();
        using var metrics = new BusinessMetrics();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter("some.other.meter")
            .AddReader(new BaseExportingMetricReader(new CapturingExporter(exported)))
            .Build();

        metrics.RecordInformationCollected(3);
        meterProvider!.ForceFlush(10_000).Should().BeTrue();

        exported.Should().NotContain(BusinessMetricNames.InformationItemsCollected);
    }

    // NFR-01, NFR-02, #689, IADR-0307 決定6: 端点間レイテンシのバケット境界に**目標値そのもの**が入っている。
    // 🔴 OTel 既定の境界は上限 10,000 ms である。境界を与えないと 5 分（300,000 ms）・10 分（600,000 ms）は
    // すべて +Inf バケットへ落ち、**分位点も「目標超過の件数」も読めない**——計器は在るのに読めない、という
    // 最も気付きにくい失敗になる。境界は 2 本のヒストグラムで共有する。
    [Fact]
    public void 端点間レイテンシのバケット境界に目標値が含まれる()
    {
        ObservabilityExtensions.TradeCycleLatencyBucketsMs.Should().ContainInOrder(300_000d, 600_000d);
        ObservabilityExtensions.TradeCycleLatencyBucketsMs.Should().BeInAscendingOrder();
    }

    // 上の境界が**実際に View として適用されている**ことを export された測定値で確かめる。
    // 定数を持っているだけでは AddView の行が消えても緑になる（配線 (2) と同じ形の失効）。
    [Theory]
    [InlineData(BusinessMetricNames.TradeCycleOrderCompletionLatencyMs)]
    [InlineData(BusinessMetricNames.TradeCycleRecordCompletionLatencyMs)]
    public void 端点間レイテンシのヒストグラムは既定ではなく明示した境界で出ていく(string instrumentName)
    {
        var bounds = new List<(string Name, double[] Bounds)>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAiStockTradingObservability(EmptyConfig(), "audit-service");
        services.ConfigureOpenTelemetryMeterProvider(builder =>
            builder.AddReader(new BaseExportingMetricReader(new BucketCapturingExporter(bounds))));

        using var provider = services.BuildServiceProvider();
        var meterProvider = provider.GetRequiredService<MeterProvider>();
        var startedAt = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);
        var metrics = provider.GetRequiredService<BusinessMetrics>();
        if (instrumentName == BusinessMetricNames.TradeCycleOrderCompletionLatencyMs)
        {
            metrics.RecordOrderCompletionLatency(
                BusinessMetrics.TriggerPriceMovement, startedAt, startedAt.AddMinutes(7));
        }
        else
        {
            metrics.RecordRecordCompletionLatency(
                BusinessMetrics.TriggerScheduled, startedAt, startedAt.AddMinutes(7));
        }

        // 戻り値は見ない（OTLP exporter は otel-collector が居ないため失敗する。上の肯定形と同じ理由）。
        meterProvider.ForceFlush(10_000);

        bounds.Should().ContainSingle(b => b.Name == instrumentName)
            .Which.Bounds.Should().Equal(ObservabilityExtensions.TradeCycleLatencyBucketsMs);
    }

    /// <summary>ヒストグラムの明示バケット境界を集める exporter（View が適用されたかを見るため）。</summary>
    private sealed class BucketCapturingExporter(List<(string Name, double[] Bounds)> sink) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.Histogram) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var bounds = new List<double>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        if (!double.IsPositiveInfinity(bucket.ExplicitBound)) bounds.Add(bucket.ExplicitBound);
                    }

                    sink.Add((metric.Name, [.. bounds]));
                }
            }

            return ExportResult.Success;
        }
    }

    /// <summary>export された Metric の名前だけを集める最小の exporter（InMemory exporter パッケージを足さないため）。</summary>
    private sealed class CapturingExporter(List<string> names) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch) names.Add(metric.Name);
            return ExportResult.Success;
        }
    }
}
