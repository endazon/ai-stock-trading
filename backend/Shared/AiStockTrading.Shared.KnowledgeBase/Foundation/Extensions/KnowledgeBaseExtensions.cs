using AiStockTrading.Shared.KnowledgeBase.Adapters;
using AiStockTrading.Shared.KnowledgeBase.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;

// FR-08, IADR-0069: KB 保存（IKnowledgeBaseWriter）・RAG 取得（IKnowledgeBaseSearch）を消費側サービスへ 1 行で配線する。
//
//   builder.Services.AddAiStockTradingKnowledgeBase(builder.Configuration);
//
// 安全既定（IADR-0069 決定 2）: `KnowledgeBase:Documents:BaseUrl` 未設定/不正 URI なら保存は NoOp（ログのみ）。
// `KnowledgeBase:Search:BaseUrl` 未設定/不正 URI なら取得は NoOp（空）。
// s2s トークン（IADR-0093）: KB は MSP `microservices-platform` レルムの専用クライアントで認証する（AddAiStockTradingKnowledgeBaseAuth）。
// `KnowledgeBase:Auth:*` 未設定ならトークンを付けない（＝401 → fail-safe）。よって既定ビルド/CI は外部接続なしで成立する。
// DocumentService と RetrievalService は別ホストのため、保存・取得それぞれ独立に BaseUrl で opt-in する。
public static class KnowledgeBaseExtensions
{
    // 保存先（DocumentService）・取得先（RetrievalService）の名前付き HttpClient。
    internal const string DocumentsClientName = "kb-documents";
    internal const string SearchClientName = "kb-search";

    // 同期経路から呼ばれ得るため保守的な短いタイムアウトを持つ。
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddAiStockTradingKnowledgeBase(
        this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        // 実接続時のみ意味を持つ名前付きクライアント（MSP レルムの KB 専用 s2s トークン付与つき・IADR-0093）。
        // BaseUrl 未設定でも登録は無害。KnowledgeBase:Auth 未設定ならトークンは付かない（fail-safe）。
        services.AddHttpClient(DocumentsClientName, c => c.Timeout = DefaultTimeout)
            .AddAiStockTradingKnowledgeBaseAuth(config);
        services.AddHttpClient(SearchClientName, c => c.Timeout = DefaultTimeout)
            .AddAiStockTradingKnowledgeBaseAuth(config);

        // 保存ポート: Documents:BaseUrl が絶対 URI なら Http、さもなくば NoOp（解決時に構成を読む）。
        services.AddSingleton<IKnowledgeBaseWriter>(sp =>
        {
            var baseUrl = config["KnowledgeBase:Documents:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return new NoOpKnowledgeBaseWriter(sp.GetRequiredService<ILogger<NoOpKnowledgeBaseWriter>>());

            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(DocumentsClientName);
            http.BaseAddress = uri;
            return new HttpKnowledgeBaseWriter(http, sp.GetRequiredService<ILogger<HttpKnowledgeBaseWriter>>());
        });

        // 取得ポート: Search:BaseUrl が絶対 URI なら Http、さもなくば NoOp。
        services.AddSingleton<IKnowledgeBaseSearch>(sp =>
        {
            var baseUrl = config["KnowledgeBase:Search:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
                return new NoOpKnowledgeBaseSearch(sp.GetRequiredService<ILogger<NoOpKnowledgeBaseSearch>>());

            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(SearchClientName);
            http.BaseAddress = uri;
            return new HttpKnowledgeBaseSearch(http, sp.GetRequiredService<ILogger<HttpKnowledgeBaseSearch>>());
        });

        return services;
    }
}
