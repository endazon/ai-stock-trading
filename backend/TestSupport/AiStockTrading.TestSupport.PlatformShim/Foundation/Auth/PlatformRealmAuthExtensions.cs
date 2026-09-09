using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// NFR-05, #724, IADR-0323: **MSP（microservices-platform）レルム**の confidential client で
// client_credentials トークンを取り、指定の名前付き HttpClient にだけ付与する汎用の登録点。
//
// なぜ AST レルムの ServiceAuth（AddAiStockTradingServiceToken）を使えないか:
//   - MSP のサービス（LlmGateway・DocumentService 等）は MSP レルムの Authority で JWT を検証するため、
//     AST レルム発行トークンは issuer 不一致で 401 になる（IADR-0093 が KB で実測した故障と同型）。
//
// なぜ DI へ provider を登録せず inline 生成するか（IADR-0093 決定2・IADR-0062 と同じ「分離」）:
//   - 同じプロセスが AddAiStockTradingServiceToken（AST レルム）も呼び、IServiceAccessTokenProvider を
//     TryAddSingleton で登録する。ここでも DI 登録すると TryAdd 衝突で先勝ちになり、**AST レルムの
//     トークンが MSP 向けクライアントへ漏れる（またはその逆）**。inline 生成は付与先の名前付き
//     クライアントにトークン発行を閉じ込め、レルム跨ぎの取り違えを構造的に防ぐ。
//
// 安全既定: 資格情報/エンドポイントが揃わなければハンドラを付けない（＝トークン無し＝現行挙動）。
// 設定不備で「誰でもない権限」で通ることはなく、上流が認可を要求していれば 401 → 呼び出し側の fail-safe へ倒れる。
//
// 🔴 セクションは**呼び出し側が明示する**（`KnowledgeBase:Auth` / `LlmGateway:Auth`）。AST の `Auth:Authority`
// （AST レルム）へはフォールバックしない —— 流用すると誤って AST レルムのトークンを出し、上の故障を再現する。
public static class PlatformRealmAuthExtensions
{
    /// <summary>
    /// 名前付き HttpClient へ MSP レルムのサービストークン付与を追加する。無効時（資格情報未整備）は何も付けない。
    /// </summary>
    /// <param name="builder">付与先の名前付き HttpClient。</param>
    /// <param name="config">構成。</param>
    /// <param name="sectionName">認証設定のセクション名（例 <c>LlmGateway:Auth</c>）。</param>
    /// <param name="tokenClientName">token 取得専用の名前付き HttpClient 名（付与の鎖を通さない＝自己再帰の回避）。</param>
    public static IHttpClientBuilder AddAiStockTradingPlatformRealmToken(
        this IHttpClientBuilder builder, IConfiguration config, string sectionName, string tokenClientName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenClientName);

        var options = ReadOptions(config, sectionName);
        if (!options.IsEnabled)
            return builder; // fail-safe: 資格情報/エンドポイント未整備ならトークンを付けない（現行挙動）。

        builder.Services.AddHttpClient(tokenClientName, c => c.Timeout = TimeSpan.FromSeconds(10));

        // provider は DI へ公開せず、本ハンドラの内部に閉じ込める（他の名前付きクライアントへ漏れない）。
        return builder.AddHttpMessageHandler(sp => new ServiceTokenHandler(
            new ClientCredentialsTokenProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(tokenClientName),
                options,
                sp.GetRequiredService<ILogger<ClientCredentialsTokenProvider>>(),
                TimeProvider.System)));
    }

    /// <summary>
    /// 指定セクションを読む。<c>TokenEndpoint</c> 未指定なら**同セクションの** <c>Authority</c>（＝MSP レルム）から導出する。
    /// AST の <c>Auth:Authority</c> へはフォールバックしない（取り違え防止）。
    /// </summary>
    public static ServiceAuthOptions ReadOptions(IConfiguration config, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        var section = config.GetSection(sectionName);
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
