using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Introspection;

// ADR-0001, FR-15（platform）, #22 受け入れ基準③: 各サービスの実効構成の自己申告を組み立て、
// メッシュ内部限定のエンドポイント（GET /internal/introspection）として公開する。
//
// 有効な段は宣言（pipeline.json・Pipeline:ConfigPath）から、ポート実装は合成ルートの構成選択から、
// ガード/構成バージョンは注入された Introspection:ConfigVersion（GitOps 注入）から導出する。
// 自己申告は照会専用・無認可（メッシュ内部限定。ingress へは公開しない。ネットワーク分離が防御）。
//
// IADR-0013: 本 shim は dev/test/CI のローカル実行のための足場。本番は platform 本体の Foundation が
// 同等の自己申告を提供する（本 shim はそれまでの代替）。
public static class IntrospectionExtensions
{
    public const string IntrospectionPath = "/internal/introspection";
    public const string ConfigVersionKey = "Introspection:ConfigVersion";

    // 自己申告 DTO を組み立てて singleton 登録する。serviceName は ServiceName 定数
    //（例: "ai-stock-trading.trade-decision-service"）。pipeline.json の service キーへ写像する。
    public static IServiceCollection AddAiStockTradingIntrospection(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<IntrospectionBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var pipelineService = ToPipelineServiceKey(serviceName);
        var steps = PipelineDeclaration.LoadStepsForService(configuration, pipelineService);

        var builder = new IntrospectionBuilder();
        configure?.Invoke(builder);

        var configVersion = configuration[ConfigVersionKey];
        var dto = new ServiceIntrospectionDto(
            pipelineService,
            steps,
            builder.Ports,
            string.IsNullOrWhiteSpace(configVersion) ? null : configVersion);

        services.AddSingleton(dto);
        return services;
    }

    // 自己申告エンドポイント（GET /internal/introspection）をマップする。無認可・メッシュ内部限定。
    public static WebApplication MapAiStockTradingIntrospection(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(IntrospectionPath,
            (ServiceIntrospectionDto report) => Results.Ok(report));
        return app;
    }

    // ServiceName 定数（"ai-stock-trading.<kebab>-service"）→ pipeline.json の service キー（"<kebab>-service"）。
    public static string ToPipelineServiceKey(string serviceName)
    {
        const string prefix = "ai-stock-trading.";
        return serviceName.StartsWith(prefix, StringComparison.Ordinal)
            ? serviceName[prefix.Length..]
            : serviceName;
    }
}

// 自己申告のポート選択を宣言的に組み立てるビルダ。段・構成バージョンは自動導出のため、
// 合成ルート固有のポート実装選択のみを申告する。
public sealed class IntrospectionBuilder
{
    private readonly List<PortSelectionDto> _ports = [];

    // 申告済みのポート実装（読み取り専用）。
    public IReadOnlyList<PortSelectionDto> Ports => _ports;

    // 選択中のポート実装を申告する（例: AddPort("broker", "moomoo"), AddPort("llm-completion", "http", baseUrl)）。
    public IntrospectionBuilder AddPort(string port, string implementation, string? target = null)
    {
        _ports.Add(new PortSelectionDto(port, implementation, target));
        return this;
    }

    // base URL の有無で Http 実装/フォールバック実装を申告する糖衣（合成ルートの選択規則と同じ源泉）。
    public IntrospectionBuilder AddPortFromBaseUrl(
        string port, string? baseUrl, string httpImplementation, string fallbackImplementation)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? AddPort(port, fallbackImplementation)
            : AddPort(port, httpImplementation, baseUrl);
    }
}
