using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace AiStockTrading.Shared.Infrastructure.Foundation.Extensions;

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
