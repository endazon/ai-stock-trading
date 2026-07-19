using AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions;

// FR-08, IADR-0093: KB 保存（DocumentService）・検索（RetrievalService）は MSP `microservices-platform` レルムの
// 専用 confidential client（`ai-stock-trading-kb-writer`／service-account に platform-operator）で認証する。
//
// なぜ AST レルムの ServiceAuth（AddAiStockTradingServiceToken）を使わないか:
//   - AST レルム発行トークンは MSP の Authority 検証で issuer 不一致→401、role も trading-service で platform-operator
//     不在→403（IADR-0093 背景）。KB は MSP レルムのトークンでなければ書き込めない。
//
// なぜ DI に provider を登録せず inline 生成するか（IADR-0062 の DiscordOwnerAuth と同じ「分離」）:
//   - 消費側 Worker は自分の s2s に AddAiStockTradingServiceToken も呼び、IServiceAccessTokenProvider を
//     TryAddSingleton で登録する。KB も DI 登録すると TryAdd 衝突で AST レルムのトークンが KB クライアントへ
//     漏れる（またはその逆）。inline 生成で KB 名前付きクライアントにトークン発行を閉じ込め、レルム跨ぎの
//     取り違えを構造的に防ぐ。
//
// 安全既定（IADR-0093 決定4）: `KnowledgeBase:Auth` の資格が揃わなければハンドラを付けない（トークン無し→401→
// writer は NotSaved へ fail-safe）。設定不備で「誰でもない権限」で通ることはない。
internal static class KnowledgeBaseAuthExtensions
{
    public const string SectionName = "KnowledgeBase:Auth";

    // token 取得専用の名前付き HttpClient（発信トークンハンドラを通さない＝自己再帰を避ける）。
    internal const string TokenClientName = "kb-writer-token";

    // KB 名前付きクライアントへ MSP レルムのサービストークン付与を追加する。無効時はハンドラを付けない。
    public static IHttpClientBuilder AddAiStockTradingKnowledgeBaseAuth(
        this IHttpClientBuilder builder, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);

        var options = ReadOptions(config);
        if (!options.IsEnabled)
            return builder; // fail-safe: 資格情報/エンドポイント未整備ならトークンを付けない（現行挙動）。

        builder.Services.AddHttpClient(TokenClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

        // provider は DI へ公開せず、本ハンドラの内部に閉じ込める（他の名前付きクライアントへ漏れない）。
        return builder.AddHttpMessageHandler(sp => new ServiceTokenHandler(
            new ClientCredentialsTokenProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenClientName),
                options,
                sp.GetRequiredService<ILogger<ClientCredentialsTokenProvider>>(),
                TimeProvider.System)));
    }

    // KnowledgeBase:Auth を読む。TokenEndpoint 未指定なら KnowledgeBase:Auth:Authority（＝MSP レルム）から導出する。
    // AST の Auth:Authority（AST レルム）へはフォールバックしない（IADR-0093 決定3・取り違え防止）。
    internal static ServiceAuthOptions ReadOptions(IConfiguration config)
    {
        var section = config.GetSection(SectionName);
        var options = new ServiceAuthOptions
        {
            TokenEndpoint = section["TokenEndpoint"],
            ClientId = section["ClientId"],
            ClientSecret = section["ClientSecret"],
            Scope = section["Scope"],
        };

        if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
        {
            var authority = section["Authority"];
            if (!string.IsNullOrWhiteSpace(authority))
                options.TokenEndpoint = authority.TrimEnd('/') + "/protocol/openid-connect/token";
        }

        if (int.TryParse(section["RefreshSkewSeconds"], out var skew) && skew >= 0)
            options.RefreshSkewSeconds = skew;

        return options;
    }
}
