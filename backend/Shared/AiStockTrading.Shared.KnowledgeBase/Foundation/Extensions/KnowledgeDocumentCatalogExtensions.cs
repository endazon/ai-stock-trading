using AiStockTrading.Shared.KnowledgeBase.Adapters;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;

// FR-08, #1028, IADR-0436 決定 2: 保守の操作（確定報告書の入れ直し）が使う文書台帳ポートを配線する。
//
//   builder.Services.AddAiStockTradingKnowledgeDocumentCatalog(builder.Configuration);
//
// 宛先・資格は業務経路の保存（AddAiStockTradingKnowledgeBase）と**同じ構成**を読む（KnowledgeBase:Documents:BaseUrl・
// KnowledgeBase:Auth＝MSP レルムの KB 専用クライアント。IADR-0093）。構成を 2 つにしない。
// 名前付きクライアントだけ分ける —— 業務経路は同期経路から呼ばれるため 5 秒で切るが、保守の操作は全文書の一覧
// （基盤は絞り込み・ページングを持たない）と 1 MB までの本文を運ぶ。
// 未構成なら NotConfigured を返す台帳（何も送らない・空の一覧のふりをしない）。
public static class KnowledgeDocumentCatalogExtensions
{
    internal const string CatalogClientName = "kb-documents-maintenance";

    internal static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddAiStockTradingKnowledgeDocumentCatalog(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        services.AddHttpClient(CatalogClientName, c => c.Timeout = CatalogTimeout)
            .AddAiStockTradingKnowledgeBaseAuth(config);

        services.AddSingleton<IKnowledgeDocumentCatalog>(sp =>
        {
            var baseUrl = config["KnowledgeBase:Documents:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return new NotConfiguredKnowledgeDocumentCatalog();

            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(CatalogClientName);
            http.BaseAddress = uri;
            return new HttpKnowledgeDocumentCatalog(http, sp.GetRequiredService<ILogger<HttpKnowledgeDocumentCatalog>>());
        });

        return services;
    }
}
