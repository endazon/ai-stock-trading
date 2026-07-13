using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Auth;

// IADR-0051: server 側 AddAiStockTradingAuth の対となる client 側トークン基盤。名前付き HttpClient へ
// ServiceTokenHandler を差し込み、client_credentials トークンを伝播する。ClientId/ClientSecret が揃い
// token エンドポイントが解決できる時のみ有効化し、未設定は no-op（現行挙動＝401 → 安全既定を保持）。
public static class ServiceAuthExtensions
{
    // 呼び出し側の名前付き HttpClient にサービストークン付与を追加する。無効時はハンドラを付けない。
    public static IHttpClientBuilder AddAiStockTradingServiceToken(
        this IHttpClientBuilder builder, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(config);

        var options = ReadOptions(config);
        if (!options.IsEnabled)
            return builder; // fail-safe: 資格情報/エンドポイント未整備なら何も付与しない（現行挙動）。

        var services = builder.Services;

        // トークン基盤は複数の名前付き HttpClient から呼ばれるため一度だけ登録する（TryAdd）。
        // provider はキャッシュを共有するため singleton。token エンドポイント用の HttpClient は
        // 発信ハンドラを通さない専用クライアント（自己再帰を避ける）。
        services.AddHttpClient(TokenClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.TryAddSingleton<IServiceAccessTokenProvider>(sp => new ClientCredentialsTokenProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(TokenClientName),
            options,
            sp.GetRequiredService<ILogger<ClientCredentialsTokenProvider>>(),
            TimeProvider.System));
        services.TryAddTransient<ServiceTokenHandler>();

        return builder.AddHttpMessageHandler<ServiceTokenHandler>();
    }

    // token エンドポイント専用の名前付き HttpClient（発信トークンハンドラを通さない）。
    private const string TokenClientName = "ai-stock-trading-service-token";

    // ServiceAuth セクションを読み、TokenEndpoint 未指定なら Auth:Authority から OIDC の token エンドポイントを導出する。
    private static ServiceAuthOptions ReadOptions(IConfiguration config)
    {
        var section = config.GetSection(ServiceAuthOptions.SectionName);
        var options = new ServiceAuthOptions
        {
            TokenEndpoint = section["TokenEndpoint"],
            ClientId = section["ClientId"],
            ClientSecret = section["ClientSecret"],
            Scope = section["Scope"],
        };

        if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
        {
            var authority = config["Auth:Authority"];
            if (!string.IsNullOrWhiteSpace(authority))
                options.TokenEndpoint = authority.TrimEnd('/') + "/protocol/openid-connect/token";
        }

        if (int.TryParse(section["RefreshSkewSeconds"], out var skew) && skew >= 0)
            options.RefreshSkewSeconds = skew;

        return options;
    }
}
