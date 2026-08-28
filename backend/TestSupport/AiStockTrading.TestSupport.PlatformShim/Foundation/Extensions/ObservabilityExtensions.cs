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
