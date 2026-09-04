using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// IADR-0011（platform ADR-0006 の最小移植・由来: Foundation/Extensions/ObservabilityExtensions.cs）:
// OTel（トレース/メトリクス）と Serilog（OTLP）への統一計装。各サービスの起動時に登録する。
public static class ObservabilityExtensions
{
    // NFR-01, NFR-02, #689, IADR-0307: 端点間レイテンシ（ミリ秒）のバケット境界。
    // **300,000（NFR-01 の 5 分）と 600,000（NFR-02 の 10 分）を境界そのものに置く** ——
    // 目標超過の件数が隣り合うバケットの差でそのまま読め、分位点の補間に頼らずに済む。
    // 本配列は 2 本のヒストグラムで共有する（片方だけ動かすと比較できなくなるため）。
    public static readonly double[] TradeCycleLatencyBucketsMs =
    [
        1_000, 5_000, 15_000, 30_000, 60_000, 120_000, 180_000, 240_000,
        300_000, 420_000, 600_000, 900_000,
    ];

    public static IServiceCollection AddAiStockTradingObservability(
        this IServiceCollection services,
        IConfiguration config,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(config);
        var otlpEndpoint = config["Otlp:Endpoint"] ?? "http://otel-collector:4317";
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(serviceName, serviceVersion: "0.1.0");

        // NFR-07, #287, IADR-0255: 業務メトリクスの計器を DI へ登録する。
        // 🔴 **計器を定義しても DI に登録されていなければ 1 系列も出ない。** 本メソッドは 11 サービス全部が
        // 通る唯一の可観測性配線であり、ここへ置くことで「あるサービスだけ計上されない」形を構造的に消す。
        services.TryAddSingleton<BusinessMetrics>();

        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resourceBuilder)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // NFR-07, #287, IADR-0255: 業務メトリクスの Meter をパイプラインへ載せる。
                // **この 1 行が消えると BusinessMetrics は記録し続けるが 1 件も外へ出ない**（無音の失効）。
                // BusinessMetricsWiringTests が否定形（AddMeter が無ければ出ない）と対で固定している。
                .AddMeter(BusinessMetricNames.MeterName)
                // NFR-01, NFR-02, #689, IADR-0307: 端点間レイテンシのバケット境界を明示する。
                // 🔴 **既定の境界は上限 10,000 ms である。** 5 分（300,000 ms）・10 分（600,000 ms）の
                // 目標値はすべて +Inf バケットへ落ち、分位点も「目標超過の件数」も読めなくなる。
                .AddView(
                    BusinessMetricNames.TradeCycleOrderCompletionLatencyMs,
                    new ExplicitBucketHistogramConfiguration { Boundaries = TradeCycleLatencyBucketsMs })
                .AddView(
                    BusinessMetricNames.TradeCycleRecordCompletionLatencyMs,
                    new ExplicitBucketHistogramConfiguration { Boundaries = TradeCycleLatencyBucketsMs })
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));

        return services;
    }

    public static void ConfigureAiStockTradingSerilog(
        this LoggerConfiguration loggerConfig,
        IConfiguration config,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(loggerConfig);
        ArgumentNullException.ThrowIfNull(config);
        var otlpEndpoint = config["Otlp:Endpoint"] ?? "http://otel-collector:4317";
        loggerConfig
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("ServiceName", serviceName)
            .WriteTo.Console()
            .WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = otlpEndpoint;
                opts.Protocol = OtlpProtocol.Grpc;
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = serviceName
                };
            });
    }
}
